import pygame
from world import TileType

class Player:
    def __init__(self, world):
        self.world = world
        self.x = 400  # Starting x position (in pixels)
        self.y = 300  # Starting y position (in pixels)
        self.speed = 5  # Player movement speed (in pixels per frame)
        self.size = 20  # Player size (in pixels)
        self.color = (255, 0, 0)  # Red color for the player

    def update(self):
        keys = pygame.key.get_pressed()
        
        new_x, new_y = self.x, self.y

        if keys[pygame.K_LEFT]:
            new_x -= self.speed
        if keys[pygame.K_RIGHT]:
            new_x += self.speed
        if keys[pygame.K_UP]:
            new_y -= self.speed
        if keys[pygame.K_DOWN]:
            new_y += self.speed
            
        if (keys[pygame.K_LEFT] or keys[pygame.K_RIGHT]) and (keys[pygame.K_UP] or keys[pygame.K_DOWN]):
            new_x = self.x + (new_x - self.x) / 1.414
            new_y = self.y + (new_y - self.y) / 1.414

        # Check if the new position is valid (not in water or out of bounds)
        if self.is_valid_position(new_x, new_y):
            self.x, self.y = new_x, new_y

        # Update the camera to follow the player
        self.world.update_camera(self.x, self.y)

    def is_valid_position(self, x, y):
        # Convert pixel coordinates to tile coordinates
        tile_x = x // self.world.tile_size
        tile_y = y // self.world.tile_size

        # Check if the position is within the world bounds
        if 0 <= tile_x < 3 * 3 * 32 and 0 <= tile_y < 3 * 3 * 32:
            # Get the tile at the new position
            region_x, sector_x = divmod(tile_x, 3 * 32)
            region_y, sector_y = divmod(tile_y, 3 * 32)
            sector_x, tile_x = divmod(sector_x, 32)
            sector_y, tile_y = divmod(sector_y, 32)

            tile = self.world.regions[region_y][region_x].sectors[sector_y][sector_x].tiles[tile_y][tile_x]

            # Check if the tile is not water
            return tile.type not in [TileType.DEEP_WATER, TileType.SHALLOW_WATER]
        
        return False

    def render(self, screen):
        screen_x = self.x - self.world.camera_x
        screen_y = self.y - self.world.camera_y
        pygame.draw.circle(screen, self.color, (int(screen_x), int(screen_y)), self.size // 2)

    def get_current_tile_info(self):
        tile_x, tile_y = int(self.x // self.world.tile_size), int(self.y // self.world.tile_size)
        region_x, sector_x = divmod(tile_x, 96)
        region_y, sector_y = divmod(tile_y, 96)
        sector_x, tile_x = divmod(sector_x, 32)
        sector_y, tile_y = divmod(sector_y, 32)

        region = self.world.regions[region_y][region_x]
        sector = region.sectors[sector_y][sector_x]
        tile = sector.tiles[tile_y][tile_x]

        return {
            "region_type": region.type,
            "sector_type": sector.type,
            "tile_type": tile.type
        }