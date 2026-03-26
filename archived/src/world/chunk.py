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
    RIVER = auto()
    WETLAND = auto()
    ARID = auto()


# Tile colors for rendering (RGB)
TILE_COLORS: dict[TileType, tuple[int, int, int]] = {
    TileType.DEEP_WATER: (0, 0, 139),
    TileType.SHALLOW_WATER: (0, 150, 200),
    TileType.SAND: (238, 214, 175),
    TileType.GRASS: (34, 139, 34),
    TileType.FOREST: (0, 100, 0),
    TileType.MOUNTAIN: (128, 128, 128),
    TileType.SNOW: (255, 250, 250),
    TileType.RIVER: (30, 120, 200),
    TileType.WETLAND: (20, 110, 70),
    TileType.ARID: (180, 140, 80),
}

# Walkable tiles for collision
WALKABLE_TILES = {TileType.SAND, TileType.GRASS, TileType.FOREST, TileType.WETLAND, TileType.ARID}


@dataclass
class Chunk:
    """A chunk of world tiles."""

    chunk_x: int
    chunk_y: int
    size: int

    # Tile data stored as numpy array for performance
    tiles: np.ndarray = field(init=False)

    # Pre-computed render data (computed in worker thread)
    render_points: list | None = field(default=None)
    render_colors: list | None = field(default=None)

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

    def compute_render_data(self, tile_size: int) -> None:
        """
        Pre-compute point and color lists for rendering.
        Call this in a worker thread after terrain generation.
        """
        chunk_world_x = self.chunk_x * self.size * tile_size
        chunk_world_y = self.chunk_y * self.size * tile_size
        half_tile = tile_size / 2

        points = []
        colors = []

        for local_y in range(self.size):
            for local_x in range(self.size):
                tile_type = TileType(self.tiles[local_y, local_x])
                color = TILE_COLORS.get(tile_type, (255, 0, 255))
                color_rgba = (color[0], color[1], color[2], 255)

                # World position of tile center
                world_x = chunk_world_x + local_x * tile_size + half_tile
                world_y = chunk_world_y + local_y * tile_size + half_tile

                # Four corners (must go around, not diagonal!)
                points.append((world_x - half_tile, world_y + half_tile))  # top_left
                points.append((world_x + half_tile, world_y + half_tile))  # top_right
                points.append((world_x + half_tile, world_y - half_tile))  # bottom_right
                points.append((world_x - half_tile, world_y - half_tile))  # bottom_left

                # 4 colors per rectangle (one per vertex)
                colors.extend([color_rgba, color_rgba, color_rgba, color_rgba])

        self.render_points = points
        self.render_colors = colors
