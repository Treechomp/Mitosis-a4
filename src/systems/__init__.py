"""ECS Systems - logic processors."""
from systems.movement import MovementProcessor
from systems.hunger import HungerProcessor
from systems.behavior import (
    WanderProcessor,
    GrazingProcessor,
    HuntingProcessor,
    FleeingProcessor,
)

__all__ = [
    "MovementProcessor",
    "HungerProcessor",
    "WanderProcessor",
    "GrazingProcessor",
    "HuntingProcessor",
    "FleeingProcessor",
]
