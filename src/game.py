"""Main game class using Arcade."""
import random
import arcade
import esper

from config import CONFIG
from world import WorldManager
from rendering import Renderer
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

        # Rendering (create first so we can use it as callback)
        self.renderer = Renderer(
            tile_size=CONFIG.TILE_SIZE,
            chunk_size=CONFIG.CHUNK_SIZE,
        )

        # Game World (with threaded chunk generation)
        self.world_manager = WorldManager(
            chunk_size=CONFIG.CHUNK_SIZE,
            world_size_chunks=CONFIG.WORLD_SIZE_CHUNKS,
            seed=CONFIG.WORLD_SEED,
            tile_size=CONFIG.TILE_SIZE,  # For pre-computing render data
            on_chunk_unload=self.renderer.remove_chunk,  # Clean renderer cache
        )

        # Camera - Arcade's built-in Camera2D
        self.game_camera = arcade.Camera2D()
        self.gui_camera = arcade.Camera2D()  # For UI elements (fixed)

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

        # Set camera to player position (in world pixels)
        self.game_camera.position = (
            spawn_x * CONFIG.TILE_SIZE,
            spawn_y * CONFIG.TILE_SIZE,
        )

        # Load immediate spawn area (blocking - needed for entities to spawn)
        player_chunk = self.world_manager.world_to_chunk(spawn_x, spawn_y)
        self.world_manager.load_immediate_area(player_chunk[0], player_chunk[1], radius=2)

        # Pre-build shapes for visible area (blocking - needed for initial render)
        for chunk in self.world_manager.get_loaded_chunks():
            self.renderer.queue_chunk_shapes(chunk)
        # Process all queued shapes immediately for initial view
        while self.renderer.get_pending_shapes_count() > 0:
            self.renderer.process_shape_queue()

        # Queue remaining chunks for gradual loading
        self.world_manager.update_streaming(spawn_x, spawn_y)

        # Spawn some test entities
        self._spawn_test_entities(spawn_x, spawn_y, count=50)

        # Update entity count (count entities with Position component)
        self.entity_count = sum(1 for _ in esper.get_component(Position))

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

        # Update camera to follow player (smooth lerp) and stream chunks
        if self.player_entity is not None:
            pos = esper.component_for_entity(self.player_entity, Position)
            target_x = pos.x * CONFIG.TILE_SIZE
            target_y = pos.y * CONFIG.TILE_SIZE

            # Smooth camera follow
            lerp = 0.1
            cam_x, cam_y = self.game_camera.position
            new_x = cam_x + (target_x - cam_x) * lerp
            new_y = cam_y + (target_y - cam_y) * lerp
            self.game_camera.position = (new_x, new_y)

            # Update chunk streaming based on player position
            self.world_manager.update_streaming(pos.x, pos.y)

        # Process completed chunk generations (from background threads)
        self.world_manager.process_completed_chunks()

        # Process shape building queue (builds shapes for newly loaded chunks)
        self.renderer.process_shape_queue()

        self.entity_count = sum(1 for _ in esper.get_component(Position))

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

        # Use game camera for world rendering
        self.game_camera.use()

        # Render world tiles (batched)
        self.renderer.render_world(self.world_manager, self.game_camera)

        # Render entities (in world coordinates, camera handles transform)
        positions = []
        renderables = []

        for entity, (pos, rend) in esper.get_components(Position, Renderable):
            positions.append((pos.x, pos.y))
            renderables.append((rend.color, rend.size, rend.shape))

        self.renderer.render_entities(positions, renderables)

        # Switch to GUI camera for UI (no transformation)
        self.gui_camera.use()

        player_pos = (0.0, 0.0)
        if self.player_entity is not None:
            pos = esper.component_for_entity(self.player_entity, Position)
            player_pos = (pos.x, pos.y)

        self.renderer.render_debug_info(
            fps=self.fps,
            entity_count=self.entity_count,
            chunk_count=len(self.world_manager.chunks),
            player_pos=player_pos,
            pending_chunks=self.world_manager.get_pending_count(),
        )

    def on_key_press(self, key: int, modifiers: int) -> None:
        """Handle key press."""
        self.keys_pressed.add(key)

        if key == arcade.key.EQUAL or key == arcade.key.PLUS:
            self.game_camera.zoom = min(4.0, self.game_camera.zoom * 1.2)
        elif key == arcade.key.MINUS:
            self.game_camera.zoom = max(0.25, self.game_camera.zoom / 1.2)

    def on_key_release(self, key: int, modifiers: int) -> None:
        """Handle key release."""
        self.keys_pressed.discard(key)

    def on_close(self) -> None:
        """Clean up when window closes."""
        self.world_manager.shutdown()
        super().on_close()


def main() -> None:
    """Main entry point."""
    game = Game()
    game.setup()
    arcade.run()


if __name__ == "__main__":
    main()
