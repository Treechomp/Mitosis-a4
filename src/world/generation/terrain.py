"""Terrain generation using noise."""
import numpy as np
from opensimplex import OpenSimplex

from ..chunk import Chunk, TileType


class TerrainGenerator:
    """Generates terrain using layered noise."""

    def __init__(self, seed: int = 42):
        self.seed = seed
        # Different noise generators for different layers
        self.elevation_noise = OpenSimplex(seed=seed)
        self.moisture_noise = OpenSimplex(seed=seed + 1000)
        self.detail_noise = OpenSimplex(seed=seed + 2000)

    def generate_chunk(self, chunk: Chunk) -> None:
        """Generate terrain for a chunk."""
        world_offset_x = chunk.chunk_x * chunk.size
        world_offset_y = chunk.chunk_y * chunk.size

        for local_y in range(chunk.size):
            for local_x in range(chunk.size):
                world_x = world_offset_x + local_x
                world_y = world_offset_y + local_y

                # Generate elevation using multiple octaves
                elevation = self._get_elevation(world_x, world_y)

                # Generate moisture
                moisture = self._get_moisture(world_x, world_y)

                # Determine tile type
                tile_type = self._elevation_to_tile(elevation, moisture)
                chunk.set_tile(local_x, local_y, tile_type)

        chunk.is_generated = True

    def _get_elevation(self, x: float, y: float) -> float:
        """Get elevation value (0-1) using domain warping."""
        # Domain warping for organic shapes
        warp_scale = 0.005
        warp_strength = 30.0

        warp_x = self.detail_noise.noise2(x * warp_scale, y * warp_scale) * warp_strength
        warp_y = self.detail_noise.noise2(
            x * warp_scale + 100, y * warp_scale + 100
        ) * warp_strength

        # Apply warped coordinates
        wx = x + warp_x
        wy = y + warp_y

        # Multi-octave noise
        value = 0.0
        amplitude = 1.0
        frequency = 0.008
        max_value = 0.0

        for _ in range(6):  # 6 octaves
            value += self.elevation_noise.noise2(wx * frequency, wy * frequency) * amplitude
            max_value += amplitude
            amplitude *= 0.5
            frequency *= 2.0

        # Normalize to 0-1
        return (value / max_value + 1) / 2

    def _get_moisture(self, x: float, y: float) -> float:
        """Get moisture value (0-1)."""
        scale = 0.015
        value = self.moisture_noise.noise2(x * scale, y * scale)
        return (value + 1) / 2

    def _elevation_to_tile(self, elevation: float, moisture: float) -> TileType:
        """Convert elevation and moisture to tile type."""
        # Water levels
        if elevation < 0.3:
            return TileType.DEEP_WATER
        if elevation < 0.38:
            return TileType.SHALLOW_WATER

        # Beach/sand near water
        if elevation < 0.42:
            return TileType.SAND

        # Main land
        if elevation < 0.7:
            if moisture < 0.4:
                return TileType.SAND  # Dry = desert-like
            elif moisture < 0.6:
                return TileType.GRASS
            else:
                return TileType.FOREST

        # Mountains
        if elevation < 0.85:
            return TileType.MOUNTAIN

        # Snow peaks
        return TileType.SNOW
