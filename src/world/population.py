"""World population - spawns initial creatures across the world."""
import random
import esper

from world.chunk import TileType, WALKABLE_TILES
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


def populate_world(world_manager, creatures_per_chunk: float = 2.0, herbivore_ratio: float = 0.8):
    """
    Populate the world with creatures based on terrain.

    Args:
        world_manager: The WorldManager with pre-generated chunks
        creatures_per_chunk: Average creatures to spawn per chunk
        herbivore_ratio: Ratio of herbivores to carnivores (0.0-1.0)
    """
    total_spawned = 0

    for chunk in world_manager.get_loaded_chunks():
        # Count grass/forest tiles to determine spawn density
        tile_counts = world_manager.count_tiles_in_chunk(chunk)
        grass_count = tile_counts.get(TileType.GRASS, 0) + tile_counts.get(TileType.FOREST, 0)

        # More vegetation = more creatures
        vegetation_ratio = grass_count / (chunk.size * chunk.size)
        spawn_count = int(creatures_per_chunk * vegetation_ratio * 2)

        if spawn_count == 0:
            continue

        # Get walkable positions
        positions = world_manager.get_walkable_positions_in_chunk(chunk, spawn_count)

        for x, y in positions:
            if random.random() < herbivore_ratio:
                spawn_herbivore(x, y)
            else:
                spawn_carnivore(x, y)
            total_spawned += 1

    return total_spawned


def spawn_herbivore(x: float, y: float, generation: int = 0) -> int:
    """Spawn a herbivore at the given position."""
    # Random starting age (some mature, some young)
    starting_age = random.randint(0, 1500)

    return esper.create_entity(
        Position(x=x, y=y),
        Velocity(),
        ChunkPosition(),
        Species(type=SpeciesType.HERBIVORE, generation=generation),
        Hunger(current=random.uniform(60.0, 100.0)),
        Energy(current=random.uniform(70.0, 100.0)),
        Age(current=starting_age, max_lifespan=8000, maturity_age=800),
        Reproduction(
            hunger_threshold=75.0,
            energy_threshold=85.0,
            cooldown=400,
        ),
        Wander(speed=0.05, change_direction_chance=0.01),
        Prey(flee_range=6.0, flee_speed_multiplier=1.8),
        Renderable(color=(100, 255, 100), size=8.0, shape="circle"),
    )


def spawn_carnivore(x: float, y: float, generation: int = 0) -> int:
    """Spawn a carnivore at the given position."""
    starting_age = random.randint(0, 1200)

    return esper.create_entity(
        Position(x=x, y=y),
        Velocity(),
        ChunkPosition(),
        Species(type=SpeciesType.CARNIVORE, generation=generation),
        Hunger(current=random.uniform(40.0, 80.0), decay_rate=0.15),
        Energy(current=random.uniform(70.0, 100.0)),
        Age(current=starting_age, max_lifespan=6000, maturity_age=600),
        Reproduction(
            hunger_threshold=80.0,
            energy_threshold=90.0,
            cooldown=600,
        ),
        Wander(speed=0.08, change_direction_chance=0.015),
        Predator(hunt_range=8.0, attack_power=35.0),
        Renderable(color=(255, 100, 100), size=10.0, shape="triangle"),
    )


def run_simulation_warmup(ticks: int = 100, progress_callback=None):
    """
    Run the simulation for a number of ticks to let ecosystems establish.

    Args:
        ticks: Number of simulation ticks to run
        progress_callback: Optional callable(completed, total) for progress updates
    """
    for i in range(ticks):
        esper.process()
        if progress_callback and i % 10 == 0:
            progress_callback(i, ticks)

    if progress_callback:
        progress_callback(ticks, ticks)
