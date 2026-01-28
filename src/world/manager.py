"""World manager for chunk loading and management."""
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
    load_radius: int = 4  # Chunks to load around player
    unload_radius: int = 6  # Chunks beyond this are unloaded

    # Chunk storage
    chunks: dict[tuple[int, int], Chunk] = field(default_factory=dict)

    # Terrain generator
    generator: TerrainGenerator = field(init=False)

    # Spatial hash for entities
    spatial_hash: SpatialHash = field(init=False)

    # Currently loaded chunk range (for streaming)
    loaded_range: tuple[int, int, int, int] = field(
        default=(0, 0, 0, 0)
    )  # min_x, min_y, max_x, max_y

    # Track player's last chunk for change detection
    _last_player_chunk: tuple[int, int] = field(default=(0, 0))

    # Callback for when chunks are unloaded (for renderer cleanup)
    on_chunk_unload: Callable[[int, int], None] | None = field(default=None)

    def __post_init__(self) -> None:
        """Initialize generator and spatial hash."""
        self.generator = TerrainGenerator(seed=self.seed)
        self.spatial_hash = SpatialHash(cell_size=float(self.chunk_size))

    def get_chunk(self, chunk_x: int, chunk_y: int) -> Chunk | None:
        """Get a chunk, generating it if needed."""
        # Bounds check
        if not (0 <= chunk_x < self.world_size_chunks and
                0 <= chunk_y < self.world_size_chunks):
            return None

        key = (chunk_x, chunk_y)

        # Generate if not exists
        if key not in self.chunks:
            chunk = Chunk(chunk_x=chunk_x, chunk_y=chunk_y, size=self.chunk_size)
            self.generator.generate_chunk(chunk)
            self.chunks[key] = chunk

        return self.chunks[key]

    def get_tile(self, world_x: float, world_y: float):
        """Get tile type at world coordinates."""
        chunk_x = int(world_x // self.chunk_size)
        chunk_y = int(world_y // self.chunk_size)

        chunk = self.get_chunk(chunk_x, chunk_y)
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

        chunk = self.get_chunk(chunk_x, chunk_y)
        if chunk is None:
            return False

        local_x = int(world_x) % self.chunk_size
        local_y = int(world_y) % self.chunk_size
        return chunk.is_walkable(local_x, local_y)

    def load_chunks_around(self, center_x: int, center_y: int, radius: int) -> None:
        """Load all chunks within radius of a center chunk."""
        min_x = max(0, center_x - radius)
        max_x = min(self.world_size_chunks - 1, center_x + radius)
        min_y = max(0, center_y - radius)
        max_y = min(self.world_size_chunks - 1, center_y + radius)

        for cx in range(min_x, max_x + 1):
            for cy in range(min_y, max_y + 1):
                self.get_chunk(cx, cy)

        self.loaded_range = (min_x, min_y, max_x, max_y)

    def update_streaming(self, player_x: float, player_y: float) -> bool:
        """
        Update chunk streaming based on player position.

        Returns True if chunks were loaded/unloaded (player moved to new chunk).
        """
        # Get player's current chunk
        player_chunk = self.world_to_chunk(player_x, player_y)

        # Only update if player moved to a different chunk
        if player_chunk == self._last_player_chunk:
            return False

        self._last_player_chunk = player_chunk
        center_x, center_y = player_chunk

        # Load new chunks around player
        self.load_chunks_around(center_x, center_y, self.load_radius)

        # Unload distant chunks
        self._unload_distant_chunks(center_x, center_y)

        return True

    def _unload_distant_chunks(self, center_x: int, center_y: int) -> None:
        """Unload chunks that are too far from the center."""
        chunks_to_unload = []

        for (cx, cy) in self.chunks.keys():
            # Calculate Chebyshev distance (max of x and y distance)
            distance = max(abs(cx - center_x), abs(cy - center_y))

            if distance > self.unload_radius:
                chunks_to_unload.append((cx, cy))

        # Unload chunks and notify callback
        for cx, cy in chunks_to_unload:
            del self.chunks[(cx, cy)]

            # Notify renderer to clean up cached shapes
            if self.on_chunk_unload is not None:
                self.on_chunk_unload(cx, cy)

    def get_loaded_chunks(self) -> list[Chunk]:
        """Get list of all currently loaded chunks."""
        return list(self.chunks.values())

    def world_to_chunk(self, world_x: float, world_y: float) -> tuple[int, int]:
        """Convert world coordinates to chunk coordinates."""
        return (int(world_x // self.chunk_size), int(world_y // self.chunk_size))
