"""Main game class using Arcade."""
import random
import arcade
import esper

from config import CONFIG
from world import WorldManager, populate_world
from rendering import Renderer
from components import (
    Position,
    Velocity,
    ChunkPosition,
    Species,
    SpeciesType,
    Hunger,
    Energy,
    Age,
    Reproduction,
    Wander,
    Renderable,
    Predator,
    Prey,
)
from systems import (
    MovementProcessor,
    HungerProcessor,
    WanderProcessor,
    GrazingProcessor,
    HuntingProcessor,
    FleeingProcessor,
    AgingProcessor,
    ReproductionProcessor,
)


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

    def setup(self, pregenerate: bool = True, pregenerate_radius: int = 16) -> None:
        """
        Set up the game.

        Args:
            pregenerate: Whether to pre-generate world chunks before starting
            pregenerate_radius: Radius of chunks to pre-generate around center
        """
        # Register ECS processors (esper 3.0 module-level API)
        esper.add_processor(MovementProcessor(self.world_manager, CONFIG.CHUNK_SIZE))
        esper.add_processor(HungerProcessor())
        esper.add_processor(WanderProcessor())
        esper.add_processor(GrazingProcessor(self.world_manager))
        esper.add_processor(HuntingProcessor())
        esper.add_processor(FleeingProcessor())
        esper.add_processor(AgingProcessor())
        esper.add_processor(ReproductionProcessor(self.world_manager, max_population=1000))

        # Player spawn position (center of world)
        spawn_x = CONFIG.WORLD_SIZE_CHUNKS * CONFIG.CHUNK_SIZE / 2
        spawn_y = CONFIG.WORLD_SIZE_CHUNKS * CONFIG.CHUNK_SIZE / 2
        player_chunk = self.world_manager.world_to_chunk(spawn_x, spawn_y)

        if pregenerate:
            print(f"Pre-generating world (radius {pregenerate_radius} chunks)...")
            self._pregenerate_area(player_chunk[0], player_chunk[1], pregenerate_radius)
            print(f"Populating world with creatures...")
            creature_count = populate_world(
                self.world_manager,
                creatures_per_chunk=3.0,
                herbivore_ratio=0.85,
            )
            print(f"Spawned {creature_count} creatures")
        else:
            # Load immediate spawn area only
            self.world_manager.load_immediate_area(player_chunk[0], player_chunk[1], radius=2)
            self._spawn_test_entities(spawn_x, spawn_y, count=50)

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
            Renderable(color=(255, 255, 0), size=12.0, shape="circle"),  # Yellow player
        )

        # Set camera to player position (in world pixels)
        self.game_camera.position = (
            spawn_x * CONFIG.TILE_SIZE,
            spawn_y * CONFIG.TILE_SIZE,
        )

        # Pre-build shapes for visible area (blocking - needed for initial render)
        for chunk in self.world_manager.get_loaded_chunks():
            self.renderer.queue_chunk_shapes(chunk)
        while self.renderer.get_pending_shapes_count() > 0:
            self.renderer.process_shape_queue()

        # Queue remaining chunks for gradual loading
        self.world_manager.update_streaming(spawn_x, spawn_y)

        # Update entity count
        self.entity_count = sum(1 for _ in esper.get_component(Position))
        print(f"Setup complete. {self.entity_count} entities, {len(self.world_manager.chunks)} chunks loaded")

    def _pregenerate_area(self, center_x: int, center_y: int, radius: int) -> None:
        """Pre-generate chunks in an area."""
        min_x = max(0, center_x - radius)
        max_x = min(CONFIG.WORLD_SIZE_CHUNKS - 1, center_x + radius)
        min_y = max(0, center_y - radius)
        max_y = min(CONFIG.WORLD_SIZE_CHUNKS - 1, center_y + radius)

        total = (max_x - min_x + 1) * (max_y - min_y + 1)
        generated = 0

        for cx in range(min_x, max_x + 1):
            for cy in range(min_y, max_y + 1):
                self.world_manager.get_or_generate_chunk(cx, cy)
                generated += 1
                if generated % 100 == 0:
                    print(f"  Generated {generated}/{total} chunks...")

    def _spawn_test_entities(self, center_x: float, center_y: float, count: int) -> None:
        """Spawn test entities around a position."""
        for _ in range(count):
            x = center_x + random.uniform(-30, 30)
            y = center_y + random.uniform(-30, 30)

            if not self.world_manager.is_walkable(x, y):
                continue

            if random.random() < 0.8:
                # Herbivore - grazes on grass/forest, flees from predators
                esper.create_entity(
                    Position(x=x, y=y),
                    Velocity(),
                    ChunkPosition(),
                    Species(type=SpeciesType.HERBIVORE),
                    Hunger(current=80.0),
                    Energy(current=100.0),
                    Age(current=500, max_lifespan=8000, maturity_age=800),
                    Reproduction(
                        hunger_threshold=75.0,
                        energy_threshold=85.0,
                        cooldown=400,
                    ),
                    Wander(speed=0.05, change_direction_chance=0.01),
                    Prey(flee_range=6.0, flee_speed_multiplier=1.8),
                    Renderable(color=(100, 255, 100), size=8.0, shape="circle"),
                )
            else:
                # Carnivore - hunts herbivores for food
                esper.create_entity(
                    Position(x=x, y=y),
                    Velocity(),
                    ChunkPosition(),
                    Species(type=SpeciesType.CARNIVORE),
                    Hunger(current=60.0, decay_rate=0.15),
                    Energy(current=100.0),
                    Age(current=400, max_lifespan=6000, maturity_age=600),
                    Reproduction(
                        hunger_threshold=80.0,
                        energy_threshold=90.0,
                        cooldown=600,
                    ),
                    Wander(speed=0.08, change_direction_chance=0.015),
                    Predator(hunt_range=8.0, attack_power=35.0),
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
