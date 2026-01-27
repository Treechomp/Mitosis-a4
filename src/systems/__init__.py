"""ECS Systems - logic processors."""
from systems.movement import MovementProcessor
from systems.hunger import HungerProcessor
from systems.behavior import WanderProcessor

__all__ = [
    "MovementProcessor",
    "HungerProcessor",
    "WanderProcessor",
]
