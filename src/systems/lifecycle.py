"""Lifecycle systems: aging, reproduction, and natural death."""
import random
import math
from typing import Callable

import esper

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


class AgingProcessor(esper.Processor):
    """Ages entities and handles natural death."""

    def process(self) -> None:
        """Update age for all entities with Age component."""
        entities_to_kill = []

        for entity, (age, energy) in esper.get_components(Age, Energy):
            age.current += 1

            # Natural death from old age
            if age.current >= age.max_lifespan:
                entities_to_kill.append(entity)

        for entity in entities_to_kill:
            try:
                esper.delete_entity(entity)
            except KeyError:
                pass


class ReproductionProcessor(esper.Processor):
    """Handles reproduction for mature entities with sufficient resources."""

    def __init__(self, world_manager, max_population: int = 500):
        self.world_manager = world_manager
        self.max_population = max_population

    def process(self) -> None:
        """Check and process reproduction for eligible entities."""
        # Count current population to prevent explosion
        current_pop = sum(1 for _ in esper.get_component(Position))
        if current_pop >= self.max_population:
            return

        offspring_to_create = []

        for entity, (pos, species, hunger, energy, age, repro) in esper.get_components(
            Position, Species, Hunger, Energy, Age, Reproduction
        ):
            # Reduce cooldown
            if repro.current_cooldown > 0:
                repro.current_cooldown -= 1
                continue

            # Check if mature enough
            if not age.is_mature:
                continue

            # Check resource thresholds
            if hunger.current < repro.hunger_threshold:
                continue
            if energy.current < repro.energy_threshold:
                continue

            # Reproduce!
            for _ in range(repro.offspring_count):
                # Find spawn position
                angle = random.uniform(0, 2 * math.pi)
                distance = random.uniform(1.0, repro.spawn_radius)
                offspring_x = pos.x + math.cos(angle) * distance
                offspring_y = pos.y + math.sin(angle) * distance

                # Check if spawn location is walkable
                if not self.world_manager.is_walkable(offspring_x, offspring_y):
                    continue

                offspring_to_create.append((
                    offspring_x,
                    offspring_y,
                    species.type,
                    species.generation + 1,
                ))

            # Apply reproduction cost
            hunger.current -= repro.hunger_cost
            energy.current -= repro.energy_cost
            repro.current_cooldown = repro.cooldown

            # Stop if we'd exceed population
            if current_pop + len(offspring_to_create) >= self.max_population:
                break

        # Create offspring entities
        for x, y, species_type, generation in offspring_to_create:
            self._create_offspring(x, y, species_type, generation)

    def _create_offspring(
        self, x: float, y: float, species_type: SpeciesType, generation: int
    ) -> int:
        """Create an offspring entity of the given species."""
        if species_type == SpeciesType.HERBIVORE:
            return esper.create_entity(
                Position(x=x, y=y),
                Velocity(),
                ChunkPosition(),
                Species(type=SpeciesType.HERBIVORE, generation=generation),
                Hunger(current=50.0),  # Born somewhat hungry
                Energy(current=70.0),  # Born with less energy
                Age(current=0, max_lifespan=8000, maturity_age=800),
                Reproduction(
                    hunger_threshold=75.0,
                    energy_threshold=85.0,
                    cooldown=400,
                ),
                Wander(speed=0.05, change_direction_chance=0.01),
                Prey(flee_range=6.0, flee_speed_multiplier=1.8),
                Renderable(color=(100, 255, 100), size=8.0, shape="circle"),
            )
        elif species_type == SpeciesType.CARNIVORE:
            return esper.create_entity(
                Position(x=x, y=y),
                Velocity(),
                ChunkPosition(),
                Species(type=SpeciesType.CARNIVORE, generation=generation),
                Hunger(current=40.0, decay_rate=0.15),  # Born hungry
                Energy(current=80.0),
                Age(current=0, max_lifespan=6000, maturity_age=600),
                Reproduction(
                    hunger_threshold=80.0,
                    energy_threshold=90.0,
                    cooldown=600,  # Reproduce less often
                    offspring_count=1,
                ),
                Wander(speed=0.08, change_direction_chance=0.015),
                Predator(hunt_range=8.0, attack_power=35.0),
                Renderable(color=(255, 100, 100), size=10.0, shape="triangle"),
            )
        else:
            # Default fallback
            return esper.create_entity(
                Position(x=x, y=y),
                Velocity(),
                ChunkPosition(),
                Species(type=species_type, generation=generation),
                Hunger(current=50.0),
                Energy(current=50.0),
                Wander(speed=0.05),
                Renderable(color=(200, 200, 200), size=6.0, shape="circle"),
            )
