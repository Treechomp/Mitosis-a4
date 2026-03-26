"""Behavior systems for entity AI with spatial optimization."""
import random
import math

import numpy as np
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
from utils.math_utils import (
    distance_squared,
    normalize_vector,
    find_nearest_in_range_squared,
    calculate_flee_vector,
)


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
    """Predators hunt and eat prey entities using spatial hashing for efficiency."""

    def __init__(self, spatial_hash, hunt_nutrition: float = 50.0):
        self.spatial_hash = spatial_hash
        self.hunt_nutrition = hunt_nutrition

    def process(self) -> None:
        """Update hunting behavior for predators."""
        # Build prey lookup: entity_id -> (position, prey_component)
        prey_data = {}
        prey_positions = {}  # entity_id -> (x, y) for quick access
        for entity, (pos, prey) in esper.get_components(Position, Prey):
            prey_data[entity] = (pos, prey)
            prey_positions[entity] = (pos.x, pos.y)
            # Update spatial hash for prey
            self.spatial_hash.update(entity, pos.x, pos.y)

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

            # Find nearest prey using spatial hash (O(1) cell lookup vs O(n) scan)
            if predator.target_entity is None:
                hunt_range_sq = predator.hunt_range * predator.hunt_range
                nearby_entities = self.spatial_hash.query_radius(
                    pos.x, pos.y, predator.hunt_range
                )

                nearest_dist_sq = float("inf")
                nearest_prey = None

                for prey_entity in nearby_entities:
                    if prey_entity not in prey_positions:
                        continue
                    prey_x, prey_y = prey_positions[prey_entity]
                    dist_sq = distance_squared(pos.x, pos.y, prey_x, prey_y)

                    if dist_sq < hunt_range_sq and dist_sq < nearest_dist_sq:
                        nearest_dist_sq = dist_sq
                        nearest_prey = prey_entity

                if nearest_prey is not None:
                    predator.target_entity = nearest_prey

            # Hunt the target
            if predator.target_entity is not None and predator.target_entity in prey_data:
                prey_pos, prey_comp = prey_data[predator.target_entity]

                dx = prey_pos.x - pos.x
                dy = prey_pos.y - pos.y
                dist_sq = dx * dx + dy * dy

                # Close enough to attack? (use squared distance: 1.0^2 = 1.0)
                if dist_sq < 1.0 and predator.current_cooldown == 0:
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
                    if dist_sq > 1e-10:
                        if esper.has_component(entity, Velocity):
                            vel = esper.component_for_entity(entity, Velocity)
                            speed = 0.12  # Hunt speed (faster than wander)
                            norm_dx, norm_dy = normalize_vector(dx, dy)
                            vel.dx = norm_dx * speed
                            vel.dy = norm_dy * speed

        # Kill dead prey
        for prey_entity in entities_to_kill:
            if prey_entity in prey_data:
                del prey_data[prey_entity]
            self.spatial_hash.remove(prey_entity)
            try:
                esper.delete_entity(prey_entity)
            except KeyError:
                pass  # Already deleted


class FleeingProcessor(esper.Processor):
    """Prey flees from nearby predators using spatial hashing and Numba optimization."""

    def __init__(self, spatial_hash):
        self.spatial_hash = spatial_hash
        # Pre-allocated arrays for Numba (will resize as needed)
        self._pred_xs = np.empty(100, dtype=np.float64)
        self._pred_ys = np.empty(100, dtype=np.float64)

    def process(self) -> None:
        """Update fleeing behavior for prey."""
        # Build predator position arrays for Numba
        predator_list = []
        for entity, (pos, _) in esper.get_components(Position, Predator):
            predator_list.append((pos.x, pos.y))
            self.spatial_hash.update(entity, pos.x, pos.y)

        # Resize arrays if needed
        n_predators = len(predator_list)
        if n_predators > len(self._pred_xs):
            new_size = max(n_predators * 2, 100)
            self._pred_xs = np.empty(new_size, dtype=np.float64)
            self._pred_ys = np.empty(new_size, dtype=np.float64)

        # Fill arrays
        for i, (px, py) in enumerate(predator_list):
            self._pred_xs[i] = px
            self._pred_ys[i] = py

        # Views into filled portion
        pred_xs = self._pred_xs[:n_predators]
        pred_ys = self._pred_ys[:n_predators]

        for entity, (pos, prey, vel, wander) in esper.get_components(
            Position, Prey, Velocity, Wander
        ):
            flee_range_sq = prey.flee_range * prey.flee_range

            # Use Numba-optimized flee vector calculation
            flee_dx, flee_dy, threat_found = calculate_flee_vector(
                pos.x, pos.y, pred_xs, pred_ys, flee_range_sq
            )

            if threat_found:
                prey.is_fleeing = True
                flee_speed = wander.speed * prey.flee_speed_multiplier
                vel.dx = flee_dx * flee_speed
                vel.dy = flee_dy * flee_speed
            else:
                prey.is_fleeing = False
