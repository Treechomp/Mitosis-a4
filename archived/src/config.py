"""Game configuration and constants."""
from dataclasses import dataclass


@dataclass(frozen=True)
class Config:
    """Immutable game configuration."""

    # Display
    SCREEN_WIDTH: int = 1280
    SCREEN_HEIGHT: int = 720
    GAME_TITLE: str = "Mitosis"
    TARGET_FPS: int = 60

    # World
    CHUNK_SIZE: int = 32  # tiles per chunk (32x32)
    TILE_SIZE: int = 16  # pixels per tile
    WORLD_SIZE_CHUNKS: int = 64  # 64x64 chunks = 2048x2048 tiles

    # Simulation
    SIMULATION_TPS: int = 20  # simulation ticks per second
    LOD_LEVELS: int = 4  # 0=full, 1=reduced, 2=statistical, 3=aggregate

    # LOD distances (in chunks from player)
    LOD_FULL_RANGE: int = 2
    LOD_REDUCED_RANGE: int = 5
    LOD_STATISTICAL_RANGE: int = 10

    # LOD tick intervals
    LOD_FULL_INTERVAL: int = 1
    LOD_REDUCED_INTERVAL: int = 5
    LOD_STATISTICAL_INTERVAL: int = 30
    LOD_AGGREGATE_INTERVAL: int = 60

    # Entity limits
    MAX_ENTITIES_PER_CHUNK: int = 100
    MAX_VISIBLE_ENTITIES: int = 1000

    # World generation
    NOISE_SCALE: float = 0.02
    NOISE_OCTAVES: int = 6
    WORLD_SEED: int = 42


# Global config instance
CONFIG = Config()
