"""ECS Systems - logic processors."""
from .movement import MovementProcessor
from .hunger import HungerProcessor
from .behavior import WanderProcessor

__all__ = [
    "MovementProcessor",
    "HungerProcessor",
    "WanderProcessor",
]
