import pygame
from world import World
from player import Player

class Game:
    def __init__(self):
        pygame.init()
        self.screen_width = 800
        self.screen_height = 600
        self.screen = pygame.display.set_mode((self.screen_width, self.screen_height))
        pygame.display.set_caption("Mitosis a4")
        self.clock = pygame.time.Clock()
        self.world = World(self.screen_width, self.screen_height)
        self.world.generate_world()
        self.player = Player(self.world)
        self.font = pygame.font.Font(None, 24)

    def run(self):
        running = True
        while running:
            for event in pygame.event.get():
                if event.type == pygame.QUIT:
                    running = False

            self.update()
            self.render()
            self.clock.tick(60)

        pygame.quit()

    def update(self):
        self.world.update()
        self.player.update()

    def render(self):
        self.screen.fill((0, 0, 0))
        self.world.render(self.screen)
        self.player.render(self.screen)
        
        # Display player location info
        tile_info = self.player.get_current_tile_info()
        info_text = f"Region: {tile_info['region_type'].name}, Sector: {tile_info['sector_type'].name}, Tile: {tile_info['tile_type'].name}"
        text_surface = self.font.render(info_text, True, (255, 255, 255))
        self.screen.blit(text_surface, (10, 10))

        pygame.display.flip()