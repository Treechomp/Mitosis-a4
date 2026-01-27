"""Main game class using Arcade."""
import random
import arcade
import esper

from config import CONFIG
from world import WorldManager
from rendering import Renderer, Camera
from components import (
    Position,
    Velocity,
    ChunkPosition,
    Species,
    SpeciesType,
    Hunger,
    Energy,
    Wander,
    Renderable,
)
from systems import MovementProcessor, HungerProcessor, WanderProcessor


class Game(arcade.Window):
    """Main game window and loop."""

    def __init__(self):
        super().__init__(
            CONFIG.SCREEN_WIDTH,
            CONFIG.SCREEN_HEIGHT,
            CONFIG.GAME_TITLE,
        )
        arcade.set_background_color((20, 20, 30))

        # Game World
        self.world_manager = WorldManager(
            chunk_size=CONFIG.CHUNK_SIZE,
            world_size_chunks=CONFIG.WORLD_SIZE_CHUNKS,
            seed=CONFIG.WORLD_SEED,
        )

        # Rendering
        self.renderer = Renderer(
            tile_size=CONFIG.TILE_SIZE,
            chunk_size=CONFIG.CHUNK_SIZE,
        )

        # Camera
        world_size_pixels = CONFIG.WORLD_SIZE_CHUNKS * CONFIG.CHUNK_SIZE * CONFIG.TILE_SIZE
        self.camera = Camera(
            viewport_width=CONFIG.SCREEN_WIDTH,
            viewport_height=CONFIG.SCREEN_HEIGHT,
            world_width=world_size_pixels,
            world_height=world_size_pixels,
        )

        # Player entity
        self.player_entity: int | None = None

        # Input state
        self.keys_pressed: set[int] = set()

        # Simulation timing
        self.simulation_accumulator: float = 0.0
        self.simulation_dt: float = 1.0 / CONFIG.SIMULATION_TPS

        # Debug info
        self.fps: float = 60.0
        self.entity_count: int = 0

    def setup(self) -> None:
        """Set up the game."""
        # Register ECS processors (esper 3.0 module-level API)
        esper.add_processor(MovementProcessor(self.world_manager, CONFIG.CHUNK_SIZE))
        esper.add_processor(HungerProcessor())
        esper.add_processor(WanderProcessor())

        # Create player entity
        spawn_x = CONFIG.WORLD_SIZE_CHUNKS * CONFIG.CHUNK_SIZE / 2
        spawn_y = CONFIG.WORLD_SIZE_CHUNKS * CONFIG.CHUNK_SIZE / 2

        # Find walkable spawn point
        for _ in range(100):
            if self.world_manager.is_walkable(spawn_x, spawn_y):
                break
            spawn_x += random.randint(-10, 10)
            spawn_y += random.randint(-10, 10)

        self.player_entity = esper.create_entity(
            Position(x=spawn_x, y=spawn_y),
            Velocity(),
            ChunkPosition(),
            Renderable(color=(255, 100, 100), size=12.0, shape="circle"),
        )

        # Set camera to player position
        self.camera.set_position(
            spawn_x * CONFIG.TILE_SIZE,
            spawn_y * CONFIG.TILE_SIZE,
        )

        # Load initial chunks around player
        player_chunk_x = int(spawn_x // CONFIG.CHUNK_SIZE)
        player_chunk_y = int(spawn_y // CONFIG.CHUNK_SIZE)
        self.world_manager.load_chunks_around(player_chunk_x, player_chunk_y, 3)

        # Spawn some test entities
        self._spawn_test_entities(spawn_x, spawn_y, count=50)

        # Update entity count
        self.entity_count = esper.get_entity_count()

    def _spawn_test_entities(self, center_x: float, center_y: float, count: int) -> None:
        """Spawn test entities around a position."""
        for _ in range(count):
            x = center_x + random.uniform(-30, 30)
            y = center_y + random.uniform(-30, 30)

            if not self.world_manager.is_walkable(x, y):
                continue

            if random.random() < 0.8:
                # Herbivore
                esper.create_entity(
                    Position(x=x, y=y),
                    Velocity(),
                    ChunkPosition(),
                    Species(type=SpeciesType.HERBIVORE),
                    Hunger(current=80.0),
                    Energy(current=100.0),
                    Wander(speed=0.05, change_direction_chance=0.01),
                    Renderable(color=(100, 255, 100), size=8.0, shape="circle"),
                )
            else:
                # Carnivore
                esper.create_entity(
                    Position(x=x, y=y),
                    Velocity(),
                    ChunkPosition(),
                    Species(type=SpeciesType.CARNIVORE),
                    Hunger(current=60.0, decay_rate=0.15),
                    Energy(current=100.0),
                    Wander(speed=0.08, change_direction_chance=0.015),
                    Renderable(color=(255, 100, 100), size=10.0, shape="triangle"),
                )

    def on_update(self, delta_time: float) -> None:
        """Update game state."""
        if delta_time > 0:
            self.fps = 1.0 / delta_time

        self._handle_player_input()

        # Fixed timestep simulation
        self.simulation_accumulator += delta_time
        while self.simulation_accumulator >= self.simulation_dt:
            esper.process()
            self.simulation_accumulator -= self.simulation_dt

        # Update camera to follow player
        if self.player_entity is not None:
            pos = esper.component_for_entity(self.player_entity, Position)
            self.camera.follow(
                pos.x * CONFIG.TILE_SIZE,
                pos.y * CONFIG.TILE_SIZE,
                lerp=0.1,
            )
            self.renderer.clear_cache()

        self.entity_count = esper.get_entity_count()

    def _handle_player_input(self) -> None:
        """Handle player movement input."""
        if self.player_entity is None:
            return

        vel = esper.component_for_entity(self.player_entity, Velocity)
        speed = 0.2

        vel.dx = 0
        vel.dy = 0

        if arcade.key.W in self.keys_pressed or arcade.key.UP in self.keys_pressed:
            vel.dy = speed
        if arcade.key.S in self.keys_pressed or arcade.key.DOWN in self.keys_pressed:
            vel.dy = -speed
        if arcade.key.A in self.keys_pressed or arcade.key.LEFT in self.keys_pressed:
            vel.dx = -speed
        if arcade.key.D in self.keys_pressed or arcade.key.RIGHT in self.keys_pressed:
            vel.dx = speed

        if vel.dx != 0 and vel.dy != 0:
            vel.dx *= 0.707
            vel.dy *= 0.707

    def on_draw(self) -> None:
        """Render the game."""
        self.clear()

        self.renderer.render_world(self.world_manager, self.camera)

        positions = []
        renderables = []

        for entity, (pos, rend) in esper.get_components(Position, Renderable):
            positions.append((pos.x, pos.y))
            renderables.append((rend.color, rend.size, rend.shape))

        self.renderer.render_entities(positions, renderables, self.camera)

        player_pos = (0.0, 0.0)
        if self.player_entity is not None:
            pos = esper.component_for_entity(self.player_entity, Position)
            player_pos = (pos.x, pos.y)

        self.renderer.render_debug_info(
            fps=self.fps,
            entity_count=self.entity_count,
            chunk_count=len(self.world_manager.chunks),
            player_pos=player_pos,
        )

    def on_key_press(self, key: int, modifiers: int) -> None:
        """Handle key press."""
        self.keys_pressed.add(key)

        if key == arcade.key.EQUAL or key == arcade.key.PLUS:
            self.camera.zoom = min(4.0, self.camera.zoom * 1.2)
            self.renderer.clear_cache()
        elif key == arcade.key.MINUS:
            self.camera.zoom = max(0.25, self.camera.zoom / 1.2)
            self.renderer.clear_cache()

    def on_key_release(self, key: int, modifiers: int) -> None:
        """Handle key release."""
        self.keys_pressed.discard(key)


def main() -> None:
    """Main entry point."""
    game = Game()
    game.setup()
    arcade.run()


if __name__ == "__main__":
    main()
