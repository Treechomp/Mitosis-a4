"""Behavior-related ECS components."""
from dataclasses import dataclass, field


@dataclass
class Predator:
    """Marks entity as a predator that hunts prey."""

    hunt_range: float = 5.0  # Range to detect prey (in tiles)
    attack_power: float = 25.0  # Damage dealt per attack
    attack_cooldown: int = 20  # Ticks between attacks
    current_cooldown: int = 0
    target_entity: int | None = None  # Entity ID of current target


@dataclass
class Prey:
    """Marks entity as prey that flees from predators."""

    flee_range: float = 8.0  # Range to detect predators (in tiles)
    flee_speed_multiplier: float = 1.5  # Speed boost when fleeing
    is_fleeing: bool = False


@dataclass
class Wander:
    """Simple wandering behavior for entities."""

    speed: float = 0.1  # Movement speed in tiles per tick
    change_direction_chance: float = 0.02  # Chance to change direction per tick
    current_direction: tuple[float, float] = field(default_factory=lambda: (0.0, 0.0))
