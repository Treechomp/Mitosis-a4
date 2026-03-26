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


@dataclass
class Terraform:
    """Species ability to modify terrain tiles over time.

    The influence_direction determines how tiles shift on the moisture spectrum:
      ARID <-> SAND <-> GRASS <-> FOREST <-> WETLAND
      -1 = drier (Sectids), +1 = wetter (Shroomers), 0 = toward GRASS (Faelings)
    """

    influence_direction: int = 0  # -1=dry, 0=balance, +1=wet
    radius: float = 2.0  # Tile radius of influence
    strength: float = 0.02  # Probability per tick of changing a tile
    cooldown: int = 10  # Ticks between terraform attempts
    current_cooldown: int = 0
