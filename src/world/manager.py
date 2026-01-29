"""World manager for chunk loading with true parallel generation using multiprocessing."""
from concurrent.futures import ProcessPoolExecutor, Future
from dataclasses import dataclass, field
from typing import Callable
import multiprocessing

from world.chunk import Chunk, TILE_COLORS, TileType
from world.generation.terrain import TerrainGenerator
from world.spatial import SpatialHash


def _generate_chunk_process(args: tuple) -> dict:
    """
    Generate a chunk in a separate process.
    Returns a dict with chunk data (can't return Chunk directly due to pickling).
    """
    chunk_x, chunk_y, chunk_size, tile_size, seed = args

    # Create generator in this process
    generator = TerrainGenerator(seed=seed)

    # Create and generate chunk
    chunk = Chunk(chunk_x=chunk_x, chunk_y=chunk_y, size=chunk_size)
    generator.generate_chunk(chunk)

    # Pre-compute render data
    chunk_world_x = chunk_x * chunk_size * tile_size
    chunk_world_y = chunk_y * chunk_size * tile_size
    half_tile = tile_size / 2

    points = []
    colors = []

    for local_y in range(chunk_size):
        for local_x in range(chunk_size):
            tile_type = TileType(chunk.tiles[local_y, local_x])
            color = TILE_COLORS.get(tile_type, (255, 0, 255))
            color_rgba = (color[0], color[1], color[2], 255)

            world_x = chunk_world_x + local_x * tile_size + half_tile
            world_y = chunk_world_y + local_y * tile_size + half_tile

            points.append((world_x - half_tile, world_y + half_tile))
            points.append((world_x + half_tile, world_y + half_tile))
            points.append((world_x + half_tile, world_y - half_tile))
            points.append((world_x - half_tile, world_y - half_tile))
            colors.extend([color_rgba, color_rgba, color_rgba, color_rgba])

    # Return serializable data
    return {
        'chunk_x': chunk_x,
        'chunk_y': chunk_y,
        'size': chunk_size,
        'tiles': chunk.tiles.tobytes(),  # Serialize numpy array
        'render_points': points,
        'render_colors': colors,
    }


@dataclass
class WorldManager:
    """Manages world chunks with multiprocessing for true parallel generation."""

    chunk_size: int
    world_size_chunks: int
    seed: int = 42
    tile_size: int = 16

    # Chunk loading settings
    load_radius: int = 8  # Larger buffer for smoother loading
    unload_radius: int = 12
    max_workers: int = field(default_factory=lambda: max(1, multiprocessing.cpu_count() - 1))

    # Chunk storage
    chunks: dict[tuple[int, int], Chunk] = field(default_factory=dict)

    # Local generator for blocking calls
    generator: TerrainGenerator = field(init=False)

    # Spatial hash for entities
    spatial_hash: SpatialHash = field(init=False)

    # Process pool for parallel generation
    _executor: ProcessPoolExecutor | None = field(default=None, init=False)
    _pending_futures: dict[tuple[int, int], Future] = field(default_factory=dict)

    # Player tracking for predictive loading
    _player_chunk: tuple[int, int] = field(default=(0, 0))
    _player_velocity: tuple[float, float] = field(default=(0.0, 0.0))
    _last_player_pos: tuple[float, float] = field(default=(0.0, 0.0))

    # Callback for chunk unload
    on_chunk_unload: Callable[[int, int], None] | None = field(default=None)

    def __post_init__(self) -> None:
        """Initialize generator, spatial hash, and process pool."""
        self.generator = TerrainGenerator(seed=self.seed)
        self.spatial_hash = SpatialHash(cell_size=float(self.chunk_size))
        # Lazy init of process pool (can't create in __post_init__ for dataclass)

    def _get_executor(self) -> ProcessPoolExecutor:
        """Get or create the process pool executor."""
        if self._executor is None:
            self._executor = ProcessPoolExecutor(max_workers=self.max_workers)
        return self._executor

    def shutdown(self) -> None:
        """Shutdown the process pool."""
        if self._executor is not None:
            self._executor.shutdown(wait=False)
            self._executor = None

    def get_chunk(self, chunk_x: int, chunk_y: int) -> Chunk | None:
        """Get a chunk if loaded, None otherwise."""
        if not (0 <= chunk_x < self.world_size_chunks and
                0 <= chunk_y < self.world_size_chunks):
            return None
        return self.chunks.get((chunk_x, chunk_y))

    def get_or_generate_chunk(self, chunk_x: int, chunk_y: int) -> Chunk | None:
        """Get a chunk, generating immediately if needed (blocking)."""
        if not (0 <= chunk_x < self.world_size_chunks and
                0 <= chunk_y < self.world_size_chunks):
            return None

        key = (chunk_x, chunk_y)
        if key in self.chunks:
            return self.chunks[key]

        # Generate synchronously
        chunk = Chunk(chunk_x=chunk_x, chunk_y=chunk_y, size=self.chunk_size)
        self.generator.generate_chunk(chunk)
        chunk.compute_render_data(self.tile_size)
        self.chunks[key] = chunk
        return chunk

    def queue_chunk_async(self, chunk_x: int, chunk_y: int) -> bool:
        """Queue a chunk for parallel generation."""
        if not (0 <= chunk_x < self.world_size_chunks and
                0 <= chunk_y < self.world_size_chunks):
            return False

        key = (chunk_x, chunk_y)
        if key in self.chunks or key in self._pending_futures:
            return False

        # Submit to process pool
        args = (chunk_x, chunk_y, self.chunk_size, self.tile_size, self.seed)
        future = self._get_executor().submit(_generate_chunk_process, args)
        self._pending_futures[key] = future
        return True

    def process_completed_chunks(self) -> int:
        """Check for completed chunk generations and add them to the world."""
        import numpy as np

        completed = 0
        completed_keys = []

        for key, future in self._pending_futures.items():
            if future.done():
                completed_keys.append(key)
                try:
                    data = future.result()

                    # Reconstruct chunk from serialized data
                    chunk = Chunk(
                        chunk_x=data['chunk_x'],
                        chunk_y=data['chunk_y'],
                        size=data['size']
                    )
                    chunk.tiles = np.frombuffer(
                        data['tiles'], dtype=np.uint8
                    ).reshape(data['size'], data['size']).copy()
                    chunk.render_points = data['render_points']
                    chunk.render_colors = data['render_colors']
                    chunk.is_generated = True

                    if key not in self.chunks:
                        self.chunks[key] = chunk
                        completed += 1

                except Exception as e:
                    print(f"Chunk generation failed for {key}: {e}")

        for key in completed_keys:
            del self._pending_futures[key]

        return completed

    def get_tile(self, world_x: float, world_y: float):
        """Get tile type at world coordinates."""
        chunk_x = int(world_x // self.chunk_size)
        chunk_y = int(world_y // self.chunk_size)

        chunk = self.get_chunk(chunk_x, chunk_y)
        if chunk is None:
            return TileType.DEEP_WATER

        local_x = int(world_x) % self.chunk_size
        local_y = int(world_y) % self.chunk_size
        return chunk.get_tile(local_x, local_y)

    def is_walkable(self, world_x: float, world_y: float) -> bool:
        """Check if world position is walkable."""
        chunk_x = int(world_x // self.chunk_size)
        chunk_y = int(world_y // self.chunk_size)

        chunk = self.get_chunk(chunk_x, chunk_y)
        if chunk is None:
            return False

        local_x = int(world_x) % self.chunk_size
        local_y = int(world_y) % self.chunk_size
        return chunk.is_walkable(local_x, local_y)

    def load_immediate_area(self, center_x: int, center_y: int, radius: int = 1) -> None:
        """Load chunks immediately (blocking)."""
        min_x = max(0, center_x - radius)
        max_x = min(self.world_size_chunks - 1, center_x + radius)
        min_y = max(0, center_y - radius)
        max_y = min(self.world_size_chunks - 1, center_y + radius)

        for cx in range(min_x, max_x + 1):
            for cy in range(min_y, max_y + 1):
                self.get_or_generate_chunk(cx, cy)

    def update_streaming(self, player_x: float, player_y: float) -> None:
        """Update chunk streaming with predictive loading."""
        # Update velocity estimate
        dx = player_x - self._last_player_pos[0]
        dy = player_y - self._last_player_pos[1]
        alpha = 0.3
        self._player_velocity = (
            self._player_velocity[0] * (1 - alpha) + dx * alpha,
            self._player_velocity[1] * (1 - alpha) + dy * alpha,
        )
        self._last_player_pos = (player_x, player_y)

        player_chunk = self.world_to_chunk(player_x, player_y)
        self._player_chunk = player_chunk
        center_x, center_y = player_chunk

        # Collect chunks to load with priority
        chunks_to_load = []
        min_x = max(0, center_x - self.load_radius)
        max_x = min(self.world_size_chunks - 1, center_x + self.load_radius)
        min_y = max(0, center_y - self.load_radius)
        max_y = min(self.world_size_chunks - 1, center_y + self.load_radius)

        for cx in range(min_x, max_x + 1):
            for cy in range(min_y, max_y + 1):
                key = (cx, cy)
                if key in self.chunks or key in self._pending_futures:
                    continue
                priority = self._calculate_priority(cx, cy, center_x, center_y)
                chunks_to_load.append((priority, cx, cy))

        # Sort and queue
        chunks_to_load.sort(key=lambda x: x[0])
        for _, cx, cy in chunks_to_load:
            self.queue_chunk_async(cx, cy)

        # Unload distant chunks
        self._unload_distant_chunks(center_x, center_y)

    def _calculate_priority(self, cx: int, cy: int, center_x: int, center_y: int) -> float:
        """Calculate loading priority (lower = higher priority)."""
        dist_x = cx - center_x
        dist_y = cy - center_y
        distance = max(abs(dist_x), abs(dist_y))

        vel_mag = (self._player_velocity[0]**2 + self._player_velocity[1]**2) ** 0.5
        if vel_mag > 0.01:
            dir_x = dist_x / max(1, abs(dist_x) + abs(dist_y))
            dir_y = dist_y / max(1, abs(dist_x) + abs(dist_y))
            vel_dir_x = self._player_velocity[0] / vel_mag
            vel_dir_y = self._player_velocity[1] / vel_mag
            alignment = dir_x * vel_dir_x + dir_y * vel_dir_y
            direction_bonus = -alignment * 2
        else:
            direction_bonus = 0

        return distance + direction_bonus

    def _unload_distant_chunks(self, center_x: int, center_y: int) -> None:
        """Unload chunks too far from center."""
        to_unload = [
            key for key in self.chunks
            if max(abs(key[0] - center_x), abs(key[1] - center_y)) > self.unload_radius
        ]
        for cx, cy in to_unload:
            del self.chunks[(cx, cy)]
            if self.on_chunk_unload:
                self.on_chunk_unload(cx, cy)

    def get_loaded_chunks(self) -> list[Chunk]:
        """Get all loaded chunks."""
        return list(self.chunks.values())

    def get_pending_count(self) -> int:
        """Get number of chunks being generated."""
        return len(self._pending_futures)

    def world_to_chunk(self, world_x: float, world_y: float) -> tuple[int, int]:
        """Convert world to chunk coordinates."""
        return (int(world_x // self.chunk_size), int(world_y // self.chunk_size))

    def pregenerate_world(self, progress_callback=None) -> None:
        """
        Pre-generate all chunks in the world using parallel processing.
        This is blocking but uses all CPU cores for speed.

        Args:
            progress_callback: Optional callable(completed, total) for progress updates
        """
        import numpy as np

        total_chunks = self.world_size_chunks * self.world_size_chunks
        completed = 0

        # Queue all chunks
        all_args = []
        for cx in range(self.world_size_chunks):
            for cy in range(self.world_size_chunks):
                if (cx, cy) not in self.chunks:
                    args = (cx, cy, self.chunk_size, self.tile_size, self.seed)
                    all_args.append(args)

        if not all_args:
            return  # All chunks already generated

        # Process in parallel batches
        executor = self._get_executor()
        futures = {executor.submit(_generate_chunk_process, args): args for args in all_args}

        for future in futures:
            try:
                data = future.result()

                # Reconstruct chunk
                chunk = Chunk(
                    chunk_x=data['chunk_x'],
                    chunk_y=data['chunk_y'],
                    size=data['size']
                )
                chunk.tiles = np.frombuffer(
                    data['tiles'], dtype=np.uint8
                ).reshape(data['size'], data['size']).copy()
                chunk.render_points = data['render_points']
                chunk.render_colors = data['render_colors']
                chunk.is_generated = True

                key = (data['chunk_x'], data['chunk_y'])
                self.chunks[key] = chunk
                completed += 1

                if progress_callback:
                    progress_callback(completed, len(all_args))

            except Exception as e:
                print(f"Chunk generation failed: {e}")

    def count_tiles_in_chunk(self, chunk: Chunk) -> dict[TileType, int]:
        """Count occurrences of each tile type in a chunk."""
        counts = {}
        for tile_value in chunk.tiles.flat:
            tile_type = TileType(tile_value)
            counts[tile_type] = counts.get(tile_type, 0) + 1
        return counts

    def get_walkable_positions_in_chunk(self, chunk: Chunk, count: int) -> list[tuple[float, float]]:
        """Get random walkable world positions within a chunk."""
        import random
        from world.chunk import WALKABLE_TILES

        walkable = []
        for local_y in range(chunk.size):
            for local_x in range(chunk.size):
                if chunk.is_walkable(local_x, local_y):
                    world_x = chunk.chunk_x * chunk.size + local_x + 0.5
                    world_y = chunk.chunk_y * chunk.size + local_y + 0.5
                    walkable.append((world_x, world_y))

        if not walkable:
            return []

        return random.sample(walkable, min(count, len(walkable)))
