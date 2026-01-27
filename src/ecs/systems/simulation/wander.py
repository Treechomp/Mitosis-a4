"""Wander behavior system for random movement."""
import random
import math

import esper

from ...components.core import Velocity
from ...components.behavior import Wander


class WanderProcessor(esper.Processor):
    """Processes wandering behavior for entities."""

    def process(self) -> None:
        """Update wandering entities."""
        for entity, (wander, vel) in self.world.get_components(Wander, Velocity):
            # Chance to change direction
            if random.random() < wander.change_direction_chance:
                # Pick a random direction
                angle = random.uniform(0, 2 * math.pi)
                wander.current_direction = (math.cos(angle), math.sin(angle))

            # Apply movement
            vel.dx = wander.current_direction[0] * wander.speed
            vel.dy = wander.current_direction[1] * wander.speed
