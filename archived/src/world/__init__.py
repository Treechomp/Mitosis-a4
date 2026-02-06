"""World management and terrain generation."""
from world.chunk import Chunk, TileType, TILE_COLORS, WALKABLE_TILES
from world.manager import WorldManager
from world.spatial import SpatialHash
from world.population import populate_world, spawn_herbivore, spawn_carnivore, run_simulation_warmup

__all__ = [
    "Chunk",
    "TileType",
    "TILE_COLORS",
    "WALKABLE_TILES",
    "WorldManager",
    "SpatialHash",
    "populate_world",
    "spawn_herbivore",
    "spawn_carnivore",
    "run_simulation_warmup",
]
