"""Behavior systems for entity AI."""
import random
import math

import esper

from ..components import Velocity, Wander


class WanderProcessor(esper.Processor):
    """Processes wandering behavior for entities."""

    def process(self) -> None:
        """Update wandering entities."""
        for entity, (wander, vel) in self.world.get_components(Wander, Velocity):
            if random.random() < wander.change_direction_chance:
                angle = random.uniform(0, 2 * math.pi)
                wander.current_direction = (math.cos(angle), math.sin(angle))

            vel.dx = wander.current_direction[0] * wander.speed
            vel.dy = wander.current_direction[1] * wander.speed
