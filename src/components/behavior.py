"""Behavior-related ECS components."""
from dataclasses import dataclass, field


@dataclass
class Predator:
    """Marks entity as a predator that hunts prey."""

    hunt_range: float = 5.0
    attack_power: float = 25.0
    attack_cooldown: int = 20
    current_cooldown: int = 0
    target_entity: int | None = None


@dataclass
class Prey:
    """Marks entity as prey that flees from predators."""

    flee_range: float = 8.0
    flee_speed_multiplier: float = 1.5
    is_fleeing: bool = False


@dataclass
class Wander:
    """Simple wandering behavior for entities."""

    speed: float = 0.1
    change_direction_chance: float = 0.02
    current_direction: tuple[float, float] = field(default_factory=lambda: (0.0, 0.0))
