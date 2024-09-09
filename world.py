Sector->Tile Structure">
import random
from enum import Enum
import pygame
import noise

class RegionType(Enum):
    MOUNTAIN = 1
    WATER = 2
    GRASSLAND = 3
    DESERT = 4
    FOREST = 5

class SectorType(Enum):
    PEAK = 1
    HIGHLANDS = 2
    LOWLANDS = 3
    BASIN = 4
    COASTAL = 5
    INLAND = 6

class TileType(Enum):
    DEEP_WATER = 1
    SHALLOW_WATER = 2
    SAND = 3
    GRASS = 4
    FOREST = 5
    MOUNTAIN = 6
    SNOW = 7

class Tile:
    def __init__(self, tile_type):
        self.type = tile_type

class Sector:
    def __init__(self, sector_type):
        self.type = sector_type
        self.tiles = [[Tile(TileType.GRASS) for _ in range(32)] for _ in range(32)]

class Region:
    def __init__(self, region_type):
        self.type = region_type
        self.sectors = [[Sector(SectorType.LOWLANDS) for _ in range(3)] for _ in range(3)]

class World:
    def __init__(self, screen_width, screen_height):
        self.width = 3 * 3 * 32  # Total width in tiles
        self.height = 3 * 3 * 32  # Total height in tiles
        self.tile_size = 20  # Size of each tile in pixels
        self.screen_width = screen_width
        self.screen_height = screen_height
        self.camera_x = 0
        self.camera_y = 0
        self.regions = [[Region(RegionType.GRASSLAND) for _ in range(3)] for _ in range(3)]

    def generate_world(self):
        # Generate base terrain using Perlin noise
        elevation = [[0 for _ in range(self.width)] for _ in range(self.height)]
        moisture = [[0 for _ in range(self.width)] for _ in range(self.height)]

        scale = 0.05
        octaves = 6
        persistence = 0.5
        lacunarity = 2.0

        for y in range(self.height):
            for x in range(self.width):
                elevation[y][x] = noise.pnoise2(x * scale, y * scale, octaves=octaves, persistence=persistence, lacunarity=lacunarity)
                moisture[y][x] = noise.pnoise2(x * scale + 1000, y * scale + 1000, octaves=octaves, persistence=persistence, lacunarity=lacunarity)

        # Normalize elevation and moisture
        min_elev, max_elev = min(min(row) for row in elevation), max(max(row) for row in elevation)
        min_moist, max_moist = min(min(row) for row in moisture), max(max(row) for row in moisture)

        for y in range(self.height):
            for x in range(self.width):
                elevation[y][x] = (elevation[y][x] - min_elev) / (max_elev - min_elev)
                moisture[y][x] = (moisture[y][x] - min_moist) / (max_moist - min_moist)

        # Assign tile types based on elevation and moisture
        for region_y in range(3):
            for region_x in range(3):
                region = self.regions[region_y][region_x]
                region_elevation = elevation[region_y*96:(region_y+1)*96][region_x*96:(region_x+1)*96]
                region_moisture = moisture[region_y*96:(region_y+1)*96][region_x*96:(region_x+1)*96]
                
                avg_elevation = sum(sum(row) for row in region_elevation) / (96 * 96)
                avg_moisture = sum(sum(row) for row in region_moisture) / (96 * 96)
                
                # Determine region type based on average elevation and moisture
                if avg_elevation > 0.7:
                    region.type = RegionType.MOUNTAIN
                elif avg_elevation < 0.3:
                    region.type = RegionType.WATER
                elif avg_moisture > 0.6:
                    region.type = RegionType.FOREST
                elif avg_moisture < 0.3:
                    region.type = RegionType.DESERT
                else:
                    region.type = RegionType.GRASSLAND

                for sector_y in range(3):
                    for sector_x in range(3):
                        sector = region.sectors[sector_y][sector_x]
                        sector_elevation = region_elevation[sector_y*32:(sector_y+1)*32][sector_x*32:(sector_x+1)*32]
                        sector_moisture = region_moisture[sector_y*32:(sector_y+1)*32][sector_x*32:(sector_x+1)*32]
                        
                        avg_sector_elevation = sum(sum(row) for row in sector_elevation) / (32 * 32)
                        
                        # Determine sector type based on average elevation within the region
                        if avg_sector_elevation > avg_elevation + 0.2:
                            sector.type = SectorType.PEAK if region.type == RegionType.MOUNTAIN else SectorType.HIGHLANDS
                        elif avg_sector_elevation < avg_elevation - 0.2:
                            sector.type = SectorType.BASIN if region.type == RegionType.WATER else SectorType.LOWLANDS
                        else:
                            sector.type = SectorType.INLAND if region.type != RegionType.WATER else SectorType.COASTAL

                        for tile_y in range(32):
                            for tile_x in range(32):
                                elev = sector_elevation[tile_y][tile_x]
                                moist = sector_moisture[tile_y][tile_x]
                                
                                # Assign tile types based on elevation, moisture, and sector type
                                if sector.type == SectorType.BASIN:
                                    tile_type = TileType.DEEP_WATER if elev < 0.4 else TileType.SHALLOW_WATER
                                elif sector.type == SectorType.COASTAL:
                                    tile_type = TileType.SHALLOW_WATER if elev < 0.5 else TileType.SAND
                                elif sector.type == SectorType.PEAK:
                                    tile_type = TileType.SNOW if moist > 0.5 else TileType.MOUNTAIN
                                elif sector.type == SectorType.HIGHLANDS:
                                    tile_type = TileType.MOUNTAIN if elev > 0.7 else TileType.FOREST if moist > 0.5 else TileType.GRASS
                                else:  # LOWLANDS or INLAND
                                    if region.type == RegionType.DESERT:
                                        tile_type = TileType.SAND
                                    elif region.type == RegionType.FOREST:
                                        tile_type = TileType.FOREST if moist > 0.4 else TileType.GRASS
                                    else:
                                        tile_type = TileType.GRASS
                                
                                sector.tiles[tile_y][tile_x] = Tile(tile_type)

        # Generate rivers
        self.generate_rivers()

    def generate_rivers(self):
        num_rivers = random.randint(3, 7)
        for _ in range(num_rivers):
            x, y = random.randint(0, self.width - 1), random.randint(0, self.height - 1)
            while self.tiles[y][x].type != TileType.MOUNTAIN:
                x, y = random.randint(0, self.width - 1), random.randint(0, self.height - 1)

            while y < self.height - 1 and self.tiles[y][x].type != TileType.DEEP_WATER:
                self.tiles[y][x] = Tile(TileType.SHALLOW_WATER)
                directions = [(0, 1), (-1, 1), (1, 1)]
                dx, dy = random.choice(directions)
                x, y = max(0, min(x + dx, self.width - 1)), y + dy
        pass

    def update_camera(self, player_x, player_y):
        self.camera_x = player_x - self.screen_width // 2
        self.camera_y = player_y - self.screen_height // 2

        max_x = self.width * self.tile_size - self.screen_width
        max_y = self.height * self.tile_size - self.screen_height
        self.camera_x = max(0, min(self.camera_x, max_x))
        self.camera_y = max(0, min(self.camera_y, max_y))
        pass

    def render(self, screen):
        start_x = self.camera_x // self.tile_size
        start_y = self.camera_y // self.tile_size
        end_x = start_x + (self.screen_width // self.tile_size) + 1
        end_y = start_y + (self.screen_height // self.tile_size) + 1

        for y in range(start_y, end_y):
            for x in range(start_x, end_x):
                if 0 <= x < self.width and 0 <= y < self.height:
                    region_x, sector_x = divmod(x, 96)
                    region_y, sector_y = divmod(y, 96)
                    sector_x, tile_x = divmod(sector_x, 32)
                    sector_y, tile_y = divmod(sector_y, 32)

                    tile = self.regions[region_y][region_x].sectors[sector_y][sector_x].tiles[tile_y][tile_x]
                    color = self.get_tile_color(tile.type)

                    screen_x = (x * self.tile_size) - self.camera_x
                    screen_y = (y * self.tile_size) - self.camera_y

                    pygame.draw.rect(screen, color, (screen_x, screen_y, self.tile_size, self.tile_size))

    def get_tile_color(self, tile_type):
        color_map = {
            TileType.DEEP_WATER: (0, 0, 139),  # Dark blue
            TileType.SHALLOW_WATER: (0, 191, 255),  # Deep sky blue
            TileType.SAND: (238, 214, 175),  # Sand
            TileType.GRASS: (34, 139, 34),  # Forest green
            TileType.FOREST: (0, 100, 0),  # Dark green
            TileType.MOUNTAIN: (128, 128, 128),  # Gray
            TileType.SNOW: (255, 250, 250),  # Snow white
        }
        return color_map.get(tile_type, (0, 0, 0))  # Default to black if tile type is not found
        pass

    def is_valid_position(self, x, y):
        tile_x, tile_y = int(x // self.tile_size), int(y // self.tile_size)
        if 0 <= tile_x < self.width and 0 <= tile_y < self.height:
            region_x, sector_x = divmod(tile_x, 96)
            region_y, sector_y = divmod(tile_y, 96)
            sector_x, tile_x = divmod(sector_x, 32)
            sector_y, tile_y = divmod(sector_y, 32)
            tile = self.regions[region_y][region_x].sectors[sector_y][sector_x].tiles[tile_y][tile_x]
            return tile.type not in [TileType.DEEP_WATER, TileType.SHALLOW_WATER]
        return False