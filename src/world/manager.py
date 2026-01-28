"""World manager for chunk loading and management."""
from collections import deque
from dataclasses import dataclass, field
from typing import Callable

from world.chunk import Chunk
from world.generation.terrain import TerrainGenerator
from world.spatial import SpatialHash


@dataclass
class WorldManager:
    """Manages world chunks and entity spatial indexing."""

    chunk_size: int
    world_size_chunks: int
    seed: int = 42

    # Chunk loading settings
    load_radius: int = 5  # Chunks to load around player (increased for pre-loading)
    unload_radius: int = 8  # Chunks beyond this are unloaded
    chunks_per_frame: int = 1  # Max chunks to generate per frame (reduced for smoothness)

    # Chunk storage
    chunks: dict[tuple[int, int], Chunk] = field(default_factory=dict)

    # Terrain generator
    generator: TerrainGenerator = field(init=False)

    # Spatial hash for entities
    spatial_hash: SpatialHash = field(init=False)

    # Chunk loading queue (priority queue would be better, but deque is simpler)
    _pending_chunks: deque[tuple[int, int]] = field(default_factory=deque)
    _queued_set: set[tuple[int, int]] = field(default_factory=set)  # Fast lookup

    # Current player chunk for prioritization
    _player_chunk: tuple[int, int] = field(default=(0, 0))

    # Callback for when chunks are unloaded (for renderer cleanup)
    on_chunk_unload: Callable[[int, int], None] | None = field(default=None)

    def __post_init__(self) -> None:
        """Initialize generator and spatial hash."""
        self.generator = TerrainGenerator(seed=self.seed)
        self.spatial_hash = SpatialHash(cell_size=float(self.chunk_size))

    def get_chunk(self, chunk_x: int, chunk_y: int) -> Chunk | None:
        """Get a chunk if loaded, None otherwise. Does NOT generate."""
        # Bounds check
        if not (0 <= chunk_x < self.world_size_chunks and
                0 <= chunk_y < self.world_size_chunks):
            return None

        return self.chunks.get((chunk_x, chunk_y))

    def get_or_generate_chunk(self, chunk_x: int, chunk_y: int) -> Chunk | None:
        """Get a chunk, generating immediately if needed (blocking)."""
        # Bounds check
        if not (0 <= chunk_x < self.world_size_chunks and
                0 <= chunk_y < self.world_size_chunks):
            return None

        key = (chunk_x, chunk_y)

        if key not in self.chunks:
            chunk = Chunk(chunk_x=chunk_x, chunk_y=chunk_y, size=self.chunk_size)
            self.generator.generate_chunk(chunk)
            self.chunks[key] = chunk
            # Remove from queue if it was pending
            self._queued_set.discard(key)

        return self.chunks[key]

    def queue_chunk(self, chunk_x: int, chunk_y: int) -> None:
        """Add a chunk to the loading queue if not already loaded/queued."""
        # Bounds check
        if not (0 <= chunk_x < self.world_size_chunks and
                0 <= chunk_y < self.world_size_chunks):
            return

        key = (chunk_x, chunk_y)

        # Skip if already loaded or queued
        if key in self.chunks or key in self._queued_set:
            return

        self._pending_chunks.append(key)
        self._queued_set.add(key)

    def process_chunk_queue(self) -> int:
        """
        Process up to chunks_per_frame chunks from the queue.
        Returns number of chunks generated.
        """
        generated = 0

        # Sort queue by distance to player (prioritize nearby chunks)
        if len(self._pending_chunks) > 1:
            self._pending_chunks = deque(sorted(
                self._pending_chunks,
                key=lambda c: max(abs(c[0] - self._player_chunk[0]),
                                  abs(c[1] - self._player_chunk[1]))
            ))

        while self._pending_chunks and generated < self.chunks_per_frame:
            key = self._pending_chunks.popleft()
            self._queued_set.discard(key)

            # Skip if somehow already loaded
            if key in self.chunks:
                continue

            chunk_x, chunk_y = key
            chunk = Chunk(chunk_x=chunk_x, chunk_y=chunk_y, size=self.chunk_size)
            self.generator.generate_chunk(chunk)
            self.chunks[key] = chunk
            generated += 1

        return generated

    def get_tile(self, world_x: float, world_y: float):
        """Get tile type at world coordinates."""
        chunk_x = int(world_x // self.chunk_size)
        chunk_y = int(world_y // self.chunk_size)

        # Use blocking generation for tile queries (needed for collision)
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

        # Use blocking generation for walkability checks
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
        Queues chunks for loading and unloads distant ones.
        """
        # Get player's current chunk
        player_chunk = self.world_to_chunk(player_x, player_y)
        self._player_chunk = player_chunk
        center_x, center_y = player_chunk

        # Queue chunks within load radius
        min_x = max(0, center_x - self.load_radius)
        max_x = min(self.world_size_chunks - 1, center_x + self.load_radius)
        min_y = max(0, center_y - self.load_radius)
        max_y = min(self.world_size_chunks - 1, center_y + self.load_radius)

        for cx in range(min_x, max_x + 1):
            for cy in range(min_y, max_y + 1):
                self.queue_chunk(cx, cy)

        # Unload distant chunks
        self._unload_distant_chunks(center_x, center_y)

    def _unload_distant_chunks(self, center_x: int, center_y: int) -> None:
        """Unload chunks that are too far from the center."""
        chunks_to_unload = []

        for (cx, cy) in self.chunks.keys():
            distance = max(abs(cx - center_x), abs(cy - center_y))
            if distance > self.unload_radius:
                chunks_to_unload.append((cx, cy))

        for cx, cy in chunks_to_unload:
            del self.chunks[(cx, cy)]
            if self.on_chunk_unload is not None:
                self.on_chunk_unload(cx, cy)

    def get_loaded_chunks(self) -> list[Chunk]:
        """Get list of all currently loaded chunks."""
        return list(self.chunks.values())

    def get_pending_count(self) -> int:
        """Get number of chunks waiting to be generated."""
        return len(self._pending_chunks)

    def world_to_chunk(self, world_x: float, world_y: float) -> tuple[int, int]:
        """Convert world coordinates to chunk coordinates."""
        return (int(world_x // self.chunk_size), int(world_y // self.chunk_size))
