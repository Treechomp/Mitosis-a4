"""Chunk data structure for world tiles."""
from dataclasses import dataclass, field
from enum import Enum, auto
import numpy as np


class TileType(Enum):
    """Types of terrain tiles."""

    DEEP_WATER = auto()
    SHALLOW_WATER = auto()
    SAND = auto()
    GRASS = auto()
    FOREST = auto()
    MOUNTAIN = auto()
    SNOW = auto()


# Tile colors for rendering (RGB)
TILE_COLORS: dict[TileType, tuple[int, int, int]] = {
    TileType.DEEP_WATER: (0, 0, 139),
    TileType.SHALLOW_WATER: (0, 150, 200),
    TileType.SAND: (238, 214, 175),
    TileType.GRASS: (34, 139, 34),
    TileType.FOREST: (0, 100, 0),
    TileType.MOUNTAIN: (128, 128, 128),
    TileType.SNOW: (255, 250, 250),
}

# Walkable tiles for collision
WALKABLE_TILES = {TileType.SAND, TileType.GRASS, TileType.FOREST}


@dataclass
class Chunk:
    """A chunk of world tiles."""

    chunk_x: int
    chunk_y: int
    size: int

    # Tile data stored as numpy array for performance
    tiles: np.ndarray = field(init=False)

    # Entity tracking for this chunk
    entity_ids: set[int] = field(default_factory=set)

    # Statistics for LOD simulation
    population_counts: dict[str, int] = field(default_factory=dict)

    # Generation state
    is_generated: bool = False

    def __post_init__(self) -> None:
        """Initialize the tile array."""
        # Store tile types as integers for numpy efficiency
        self.tiles = np.zeros((self.size, self.size), dtype=np.uint8)

    def get_tile(self, local_x: int, local_y: int) -> TileType:
        """Get tile type at local coordinates."""
        if 0 <= local_x < self.size and 0 <= local_y < self.size:
            return TileType(self.tiles[local_y, local_x])
        return TileType.DEEP_WATER  # Out of bounds = water

    def set_tile(self, local_x: int, local_y: int, tile_type: TileType) -> None:
        """Set tile type at local coordinates."""
        if 0 <= local_x < self.size and 0 <= local_y < self.size:
            self.tiles[local_y, local_x] = tile_type.value

    def is_walkable(self, local_x: int, local_y: int) -> bool:
        """Check if a tile is walkable."""
        return self.get_tile(local_x, local_y) in WALKABLE_TILES

    def world_to_local(self, world_x: float, world_y: float) -> tuple[int, int]:
        """Convert world coordinates to local chunk coordinates."""
        local_x = int(world_x) - (self.chunk_x * self.size)
        local_y = int(world_y) - (self.chunk_y * self.size)
        return local_x, local_y

    def add_entity(self, entity_id: int) -> None:
        """Add an entity to this chunk's tracking."""
        self.entity_ids.add(entity_id)

    def remove_entity(self, entity_id: int) -> None:
        """Remove an entity from this chunk's tracking."""
        self.entity_ids.discard(entity_id)
