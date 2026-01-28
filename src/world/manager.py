"""World manager for chunk loading and management with threaded generation."""
from collections import deque
from concurrent.futures import ThreadPoolExecutor, Future
from dataclasses import dataclass, field
from threading import Lock
from typing import Callable

from world.chunk import Chunk
from world.generation.terrain import TerrainGenerator
from world.spatial import SpatialHash


@dataclass
class WorldManager:
    """Manages world chunks with threaded terrain generation."""

    chunk_size: int
    world_size_chunks: int
    seed: int = 42

    # Chunk loading settings
    load_radius: int = 6  # Chunks to load around player
    unload_radius: int = 10  # Chunks beyond this are unloaded
    max_concurrent_generations: int = 4  # Thread pool size

    # Chunk storage
    chunks: dict[tuple[int, int], Chunk] = field(default_factory=dict)

    # Terrain generator (one per thread for thread safety)
    generator: TerrainGenerator = field(init=False)

    # Spatial hash for entities
    spatial_hash: SpatialHash = field(init=False)

    # Threading for chunk generation
    _executor: ThreadPoolExecutor = field(init=False)
    _pending_futures: dict[tuple[int, int], Future] = field(default_factory=dict)
    _chunks_lock: Lock = field(default_factory=Lock)

    # Player movement tracking for predictive loading
    _player_chunk: tuple[int, int] = field(default=(0, 0))
    _player_velocity: tuple[float, float] = field(default=(0.0, 0.0))
    _last_player_pos: tuple[float, float] = field(default=(0.0, 0.0))

    # Callback for when chunks are unloaded (for renderer cleanup)
    on_chunk_unload: Callable[[int, int], None] | None = field(default=None)

    def __post_init__(self) -> None:
        """Initialize generator, spatial hash, and thread pool."""
        self.generator = TerrainGenerator(seed=self.seed)
        self.spatial_hash = SpatialHash(cell_size=float(self.chunk_size))
        self._executor = ThreadPoolExecutor(
            max_workers=self.max_concurrent_generations,
            thread_name_prefix="chunk_gen"
        )

    def shutdown(self) -> None:
        """Shutdown the thread pool. Call when closing the game."""
        self._executor.shutdown(wait=False)

    def _generate_chunk_threaded(self, chunk_x: int, chunk_y: int) -> Chunk:
        """Generate a chunk (runs in worker thread)."""
        # Each thread needs its own generator for thread safety
        # But since OpenSimplex is deterministic with seed, we can share
        chunk = Chunk(chunk_x=chunk_x, chunk_y=chunk_y, size=self.chunk_size)
        self.generator.generate_chunk(chunk)
        return chunk

    def get_chunk(self, chunk_x: int, chunk_y: int) -> Chunk | None:
        """Get a chunk if loaded, None otherwise. Does NOT generate."""
        if not (0 <= chunk_x < self.world_size_chunks and
                0 <= chunk_y < self.world_size_chunks):
            return None

        with self._chunks_lock:
            return self.chunks.get((chunk_x, chunk_y))

    def get_or_generate_chunk(self, chunk_x: int, chunk_y: int) -> Chunk | None:
        """Get a chunk, generating immediately if needed (blocking)."""
        if not (0 <= chunk_x < self.world_size_chunks and
                0 <= chunk_y < self.world_size_chunks):
            return None

        key = (chunk_x, chunk_y)

        with self._chunks_lock:
            if key in self.chunks:
                return self.chunks[key]

        # Generate synchronously (blocking)
        chunk = Chunk(chunk_x=chunk_x, chunk_y=chunk_y, size=self.chunk_size)
        self.generator.generate_chunk(chunk)

        with self._chunks_lock:
            # Check again in case another thread generated it
            if key not in self.chunks:
                self.chunks[key] = chunk
            return self.chunks[key]

    def queue_chunk_async(self, chunk_x: int, chunk_y: int) -> bool:
        """Queue a chunk for async generation. Returns True if queued."""
        if not (0 <= chunk_x < self.world_size_chunks and
                0 <= chunk_y < self.world_size_chunks):
            return False

        key = (chunk_x, chunk_y)

        # Skip if already loaded or being generated
        with self._chunks_lock:
            if key in self.chunks:
                return False

        if key in self._pending_futures:
            return False

        # Submit to thread pool
        future = self._executor.submit(self._generate_chunk_threaded, chunk_x, chunk_y)
        self._pending_futures[key] = future
        return True

    def process_completed_chunks(self) -> int:
        """
        Check for completed chunk generations and add them to the world.
        Returns number of chunks completed this frame.
        """
        completed = 0
        completed_keys = []

        for key, future in self._pending_futures.items():
            if future.done():
                completed_keys.append(key)
                try:
                    chunk = future.result()
                    with self._chunks_lock:
                        if key not in self.chunks:
                            self.chunks[key] = chunk
                            completed += 1
                except Exception as e:
                    print(f"Chunk generation failed for {key}: {e}")

        # Remove completed futures
        for key in completed_keys:
            del self._pending_futures[key]

        return completed

    def get_tile(self, world_x: float, world_y: float):
        """Get tile type at world coordinates."""
        chunk_x = int(world_x // self.chunk_size)
        chunk_y = int(world_y // self.chunk_size)

        chunk = self.get_or_generate_chunk(chunk_x, chunk_y)
        if chunk is None:
            from world.chunk import TileType
            return TileType.DEEP_WATER

        local_x = int(world_x) % self.chunk_size
        local_y = int(world_y) % self.chunk_size
        return chunk.get_tile(local_x, local_y)

    def is_walkable(self, world_x: float, world_y: float) -> bool:
        """Check if world position is walkable."""
        chunk_x = int(world_x // self.chunk_size)
        chunk_y = int(world_y // self.chunk_size)

        chunk = self.get_or_generate_chunk(chunk_x, chunk_y)
        if chunk is None:
            return False

        local_x = int(world_x) % self.chunk_size
        local_y = int(world_y) % self.chunk_size
        return chunk.is_walkable(local_x, local_y)

    def load_immediate_area(self, center_x: int, center_y: int, radius: int = 1) -> None:
        """Load chunks immediately (blocking). Use sparingly, e.g., at spawn."""
        min_x = max(0, center_x - radius)
        max_x = min(self.world_size_chunks - 1, center_x + radius)
        min_y = max(0, center_y - radius)
        max_y = min(self.world_size_chunks - 1, center_y + radius)

        for cx in range(min_x, max_x + 1):
            for cy in range(min_y, max_y + 1):
                self.get_or_generate_chunk(cx, cy)

    def update_streaming(self, player_x: float, player_y: float) -> None:
        """
        Update chunk streaming based on player position.
        Uses predictive loading based on movement direction.
        """
        # Update player velocity estimate
        dx = player_x - self._last_player_pos[0]
        dy = player_y - self._last_player_pos[1]
        # Smooth velocity with exponential moving average
        alpha = 0.3
        self._player_velocity = (
            self._player_velocity[0] * (1 - alpha) + dx * alpha,
            self._player_velocity[1] * (1 - alpha) + dy * alpha,
        )
        self._last_player_pos = (player_x, player_y)

        # Get player's current chunk
        player_chunk = self.world_to_chunk(player_x, player_y)
        self._player_chunk = player_chunk
        center_x, center_y = player_chunk

        # Calculate chunks to load with priority based on distance and movement direction
        chunks_to_load = []

        min_x = max(0, center_x - self.load_radius)
        max_x = min(self.world_size_chunks - 1, center_x + self.load_radius)
        min_y = max(0, center_y - self.load_radius)
        max_y = min(self.world_size_chunks - 1, center_y + self.load_radius)

        for cx in range(min_x, max_x + 1):
            for cy in range(min_y, max_y + 1):
                key = (cx, cy)
                with self._chunks_lock:
                    if key in self.chunks:
                        continue
                if key in self._pending_futures:
                    continue

                # Calculate priority (lower = higher priority)
                priority = self._calculate_chunk_priority(cx, cy, center_x, center_y)
                chunks_to_load.append((priority, cx, cy))

        # Sort by priority and queue
        chunks_to_load.sort(key=lambda x: x[0])

        for _, cx, cy in chunks_to_load:
            self.queue_chunk_async(cx, cy)

        # Unload distant chunks
        self._unload_distant_chunks(center_x, center_y)

    def _calculate_chunk_priority(
        self, chunk_x: int, chunk_y: int, center_x: int, center_y: int
    ) -> float:
        """
        Calculate loading priority for a chunk.
        Lower values = higher priority.
        Considers distance and movement direction.
        """
        # Base priority is distance from player
        dist_x = chunk_x - center_x
        dist_y = chunk_y - center_y
        distance = max(abs(dist_x), abs(dist_y))  # Chebyshev distance

        # Direction bonus: chunks in movement direction get priority
        # Normalize velocity
        vel_mag = (self._player_velocity[0]**2 + self._player_velocity[1]**2) ** 0.5
        if vel_mag > 0.01:
            # Direction from player to chunk
            dir_x = dist_x / max(1, abs(dist_x) + abs(dist_y))
            dir_y = dist_y / max(1, abs(dist_x) + abs(dist_y))

            # Normalized velocity direction
            vel_dir_x = self._player_velocity[0] / vel_mag
            vel_dir_y = self._player_velocity[1] / vel_mag

            # Dot product: 1 if same direction, -1 if opposite
            alignment = dir_x * vel_dir_x + dir_y * vel_dir_y

            # Reduce priority (lower number) for chunks in movement direction
            direction_bonus = -alignment * 2  # -2 to +2 range
        else:
            direction_bonus = 0

        return distance + direction_bonus

    def _unload_distant_chunks(self, center_x: int, center_y: int) -> None:
        """Unload chunks that are too far from the center."""
        chunks_to_unload = []

        with self._chunks_lock:
            for (cx, cy) in self.chunks.keys():
                distance = max(abs(cx - center_x), abs(cy - center_y))
                if distance > self.unload_radius:
                    chunks_to_unload.append((cx, cy))

        for cx, cy in chunks_to_unload:
            with self._chunks_lock:
                self.chunks.pop((cx, cy), None)
            if self.on_chunk_unload is not None:
                self.on_chunk_unload(cx, cy)

    def get_loaded_chunks(self) -> list[Chunk]:
        """Get list of all currently loaded chunks."""
        with self._chunks_lock:
            return list(self.chunks.values())

    def get_pending_count(self) -> int:
        """Get number of chunks being generated."""
        return len(self._pending_futures)

    def world_to_chunk(self, world_x: float, world_y: float) -> tuple[int, int]:
        """Convert world coordinates to chunk coordinates."""
        return (int(world_x // self.chunk_size), int(world_y // self.chunk_size))
