"""World manager for chunk loading and management."""
from dataclasses import dataclass, field

from .chunk import Chunk
from .generation.terrain import TerrainGenerator
from .spatial import SpatialHash


@dataclass
class WorldManager:
    """Manages world chunks and entity spatial indexing."""

    chunk_size: int
    world_size_chunks: int
    seed: int = 42

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
            from .chunk import TileType
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

    def get_loaded_chunks(self) -> list[Chunk]:
        """Get list of all currently loaded chunks."""
        return list(self.chunks.values())

    def world_to_chunk(self, world_x: float, world_y: float) -> tuple[int, int]:
        """Convert world coordinates to chunk coordinates."""
        return (int(world_x // self.chunk_size), int(world_y // self.chunk_size))
