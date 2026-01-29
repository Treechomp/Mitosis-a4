"""Behavior systems for entity AI."""
import random
import math

import esper

from components import (
    Position,
    Velocity,
    Wander,
    Species,
    SpeciesType,
    Hunger,
    Energy,
    Predator,
    Prey,
)
from world.chunk import TileType


class WanderProcessor(esper.Processor):
    """Processes wandering behavior for entities."""

    def process(self) -> None:
        """Update wandering entities."""
        for entity, (wander, vel) in esper.get_components(Wander, Velocity):
            # Skip if entity is fleeing (Prey behavior overrides)
            if esper.has_component(entity, Prey):
                prey = esper.component_for_entity(entity, Prey)
                if prey.is_fleeing:
                    continue

            # Skip if entity is hunting (Predator behavior overrides)
            if esper.has_component(entity, Predator):
                predator = esper.component_for_entity(entity, Predator)
                if predator.target_entity is not None:
                    continue

            if random.random() < wander.change_direction_chance:
                angle = random.uniform(0, 2 * math.pi)
                wander.current_direction = (math.cos(angle), math.sin(angle))

            vel.dx = wander.current_direction[0] * wander.speed
            vel.dy = wander.current_direction[1] * wander.speed


class GrazingProcessor(esper.Processor):
    """Herbivores graze on grass and forest tiles to restore hunger."""

    def __init__(self, world_manager, graze_rate: float = 0.5):
        self.world_manager = world_manager
        self.graze_rate = graze_rate
        # Tiles that can be grazed
        self.grazeable_tiles = {TileType.GRASS, TileType.FOREST}

    def process(self) -> None:
        """Update grazing for herbivores."""
        for entity, (pos, species, hunger) in esper.get_components(
            Position, Species, Hunger
        ):
            # Only herbivores graze
            if species.type != SpeciesType.HERBIVORE:
                continue

            # Check tile type at entity position
            tile = self.world_manager.get_tile(pos.x, pos.y)
            if tile in self.grazeable_tiles:
                # Restore hunger (capped at max)
                hunger.current = min(hunger.max, hunger.current + self.graze_rate)


class HuntingProcessor(esper.Processor):
    """Predators hunt and eat prey entities."""

    def __init__(self, hunt_nutrition: float = 50.0):
        self.hunt_nutrition = hunt_nutrition  # Hunger restored from eating prey

    def process(self) -> None:
        """Update hunting behavior for predators."""
        # Build a quick lookup of prey positions
        prey_data = {}  # entity_id -> (position, prey_component)
        for entity, (pos, prey) in esper.get_components(Position, Prey):
            prey_data[entity] = (pos, prey)

        entities_to_kill = []

        for entity, (pos, predator, hunger) in esper.get_components(
            Position, Predator, Hunger
        ):
            # Reduce cooldown
            if predator.current_cooldown > 0:
                predator.current_cooldown -= 1

            # Clear target if it no longer exists
            if predator.target_entity is not None:
                if predator.target_entity not in prey_data:
                    predator.target_entity = None

            # Find nearest prey if no target
            if predator.target_entity is None:
                nearest_dist = float("inf")
                nearest_prey = None

                for prey_entity, (prey_pos, _) in prey_data.items():
                    dx = prey_pos.x - pos.x
                    dy = prey_pos.y - pos.y
                    dist = math.sqrt(dx * dx + dy * dy)

                    if dist < predator.hunt_range and dist < nearest_dist:
                        nearest_dist = dist
                        nearest_prey = prey_entity

                if nearest_prey is not None:
                    predator.target_entity = nearest_prey

            # Hunt the target
            if predator.target_entity is not None and predator.target_entity in prey_data:
                prey_pos, prey_comp = prey_data[predator.target_entity]

                dx = prey_pos.x - pos.x
                dy = prey_pos.y - pos.y
                dist = math.sqrt(dx * dx + dy * dy)

                # Close enough to attack?
                if dist < 1.0 and predator.current_cooldown == 0:
                    # Attack!
                    if esper.has_component(predator.target_entity, Energy):
                        prey_energy = esper.component_for_entity(
                            predator.target_entity, Energy
                        )
                        prey_energy.current -= predator.attack_power

                        if prey_energy.is_dead:
                            entities_to_kill.append(predator.target_entity)
                            # Restore hunger from eating
                            hunger.current = min(
                                hunger.max, hunger.current + self.hunt_nutrition
                            )
                            predator.target_entity = None

                    predator.current_cooldown = predator.attack_cooldown
                else:
                    # Move towards prey
                    if dist > 0:
                        if esper.has_component(entity, Velocity):
                            vel = esper.component_for_entity(entity, Velocity)
                            speed = 0.12  # Hunt speed (faster than wander)
                            vel.dx = (dx / dist) * speed
                            vel.dy = (dy / dist) * speed

        # Kill dead prey
        for prey_entity in entities_to_kill:
            if prey_entity in prey_data:
                del prey_data[prey_entity]
            try:
                esper.delete_entity(prey_entity)
            except KeyError:
                pass  # Already deleted


class FleeingProcessor(esper.Processor):
    """Prey flees from nearby predators."""

    def process(self) -> None:
        """Update fleeing behavior for prey."""
        # Build lookup of predator positions
        predator_positions = []
        for entity, (pos, _) in esper.get_components(Position, Predator):
            predator_positions.append((pos.x, pos.y))

        for entity, (pos, prey, vel, wander) in esper.get_components(
            Position, Prey, Velocity, Wander
        ):
            # Check for nearby predators
            flee_dx = 0.0
            flee_dy = 0.0
            threat_found = False

            for pred_x, pred_y in predator_positions:
                dx = pos.x - pred_x
                dy = pos.y - pred_y
                dist = math.sqrt(dx * dx + dy * dy)

                if dist < prey.flee_range and dist > 0:
                    # Add flee vector (away from predator)
                    flee_dx += dx / dist
                    flee_dy += dy / dist
                    threat_found = True

            if threat_found:
                prey.is_fleeing = True
                # Normalize and apply flee speed
                flee_dist = math.sqrt(flee_dx * flee_dx + flee_dy * flee_dy)
                if flee_dist > 0:
                    flee_speed = wander.speed * prey.flee_speed_multiplier
                    vel.dx = (flee_dx / flee_dist) * flee_speed
                    vel.dy = (flee_dy / flee_dist) * flee_speed
            else:
                prey.is_fleeing = False
