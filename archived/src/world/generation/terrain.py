"""Vectorized terrain generation using noise."""
import numpy as np
from opensimplex import OpenSimplex

from world.chunk import Chunk, TileType


class TerrainGenerator:
    """Generates terrain using layered noise with vectorized operations."""

    def __init__(self, seed: int = 42):
        self.seed = seed
        # Different noise generators for different layers
        self.elevation_noise = OpenSimplex(seed=seed)
        self.moisture_noise = OpenSimplex(seed=seed + 1000)
        self.detail_noise = OpenSimplex(seed=seed + 2000)
        self.river_noise = OpenSimplex(seed=seed + 3000)

    def generate_chunk(self, chunk: Chunk) -> None:
        """Generate terrain for a chunk using vectorized operations."""
        size = chunk.size
        world_offset_x = chunk.chunk_x * size
        world_offset_y = chunk.chunk_y * size

        # Create coordinate arrays for the chunk
        local_x = np.arange(size, dtype=np.float64)
        local_y = np.arange(size, dtype=np.float64)

        # World coordinates
        world_x = local_x + world_offset_x
        world_y = local_y + world_offset_y

        # Generate elevation using vectorized multi-octave noise
        elevation = self._get_elevation_vectorized(world_x, world_y)

        # Generate moisture
        moisture = self._get_moisture_vectorized(world_x, world_y)

        # Determine tile types (vectorized)
        tiles = self._elevation_to_tiles_vectorized(elevation, moisture)

        # Carve rivers through land
        tiles = self._carve_rivers(tiles, elevation, moisture, world_x, world_y)

        chunk.tiles = tiles
        chunk.is_generated = True

    def _get_elevation_vectorized(self, x: np.ndarray, y: np.ndarray) -> np.ndarray:
        """Get elevation values (0-1) using multi-octave noise - fully vectorized."""
        # Multi-octave noise using noise2array (fully vectorized)
        value = np.zeros((len(y), len(x)))
        amplitude = 1.0
        frequency = 0.008
        max_value = 0.0

        for _ in range(4):  # 4 octaves (reduced from 6 for speed)
            octave = self.elevation_noise.noise2array(x * frequency, y * frequency)
            value += octave * amplitude

            max_value += amplitude
            amplitude *= 0.5
            frequency *= 2.0

        # Add detail layer for variation
        detail = self.detail_noise.noise2array(x * 0.05, y * 0.05) * 0.1
        value += detail

        # Normalize to 0-1
        return (value / max_value + 1) / 2

    def _get_moisture_vectorized(self, x: np.ndarray, y: np.ndarray) -> np.ndarray:
        """Get moisture values (0-1) - vectorized."""
        scale = 0.015
        value = self.moisture_noise.noise2array(x * scale, y * scale)
        return (value + 1) / 2

    def _elevation_to_tiles_vectorized(
        self, elevation: np.ndarray, moisture: np.ndarray
    ) -> np.ndarray:
        """Convert elevation and moisture arrays to tile type array."""
        # Initialize with deep water
        tiles = np.full(elevation.shape, TileType.DEEP_WATER.value, dtype=np.uint8)

        # Apply conditions from highest to lowest priority (reversed order)
        # Snow peaks
        tiles = np.where(elevation >= 0.85, TileType.SNOW.value, tiles)

        # Mountains
        tiles = np.where(
            (elevation >= 0.7) & (elevation < 0.85),
            TileType.MOUNTAIN.value,
            tiles
        )

        # Main land - depends on moisture
        land_mask = (elevation >= 0.42) & (elevation < 0.7)

        # Wetland: very high moisture land
        tiles = np.where(
            land_mask & (moisture >= 0.75),
            TileType.WETLAND.value,
            tiles
        )
        tiles = np.where(
            land_mask & (moisture >= 0.55) & (moisture < 0.75),
            TileType.FOREST.value,
            tiles
        )
        tiles = np.where(
            land_mask & (moisture >= 0.35) & (moisture < 0.55),
            TileType.GRASS.value,
            tiles
        )
        tiles = np.where(
            land_mask & (moisture >= 0.2) & (moisture < 0.35),
            TileType.SAND.value,
            tiles
        )
        # Arid: very low moisture land
        tiles = np.where(
            land_mask & (moisture < 0.2),
            TileType.ARID.value,
            tiles
        )

        # Beach/sand near water
        tiles = np.where(
            (elevation >= 0.38) & (elevation < 0.42),
            TileType.SAND.value,
            tiles
        )

        # Shallow water
        tiles = np.where(
            (elevation >= 0.3) & (elevation < 0.38),
            TileType.SHALLOW_WATER.value,
            tiles
        )

        # Deep water is already the default

        return tiles

    def _carve_rivers(
        self, tiles: np.ndarray, elevation: np.ndarray, moisture: np.ndarray,
        world_x: np.ndarray, world_y: np.ndarray,
    ) -> np.ndarray:
        """Carve rivers using noise zero-crossing on land tiles."""
        # River path noise - low frequency for wide, meandering paths
        river_val = self.river_noise.noise2array(world_x * 0.012, world_y * 0.012)

        # River width varies with elevation (wider in valleys, narrower in hills)
        # Thinner threshold = narrower river
        base_threshold = 0.018
        # Wider in lower-elevation land (valleys)
        threshold = base_threshold + (0.7 - elevation) * 0.02
        threshold = np.clip(threshold, 0.01, 0.04)

        # River mask: near the noise zero-crossing AND on walkable land
        land_tiles = (
            (tiles == TileType.GRASS.value) |
            (tiles == TileType.FOREST.value) |
            (tiles == TileType.SAND.value) |
            (tiles == TileType.WETLAND.value) |
            (tiles == TileType.ARID.value)
        )
        river_mask = (np.abs(river_val) < threshold) & land_tiles

        tiles = np.where(river_mask, TileType.RIVER.value, tiles)

        # Add wetland banks along rivers (tiles adjacent to river path but not river)
        # Use a slightly wider threshold for wetland fringe
        fringe_mask = (
            (np.abs(river_val) < threshold * 2.5) &
            (np.abs(river_val) >= threshold) &
            land_tiles &
            (tiles != TileType.RIVER.value)
        )
        tiles = np.where(fringe_mask, TileType.WETLAND.value, tiles)

        return tiles
