"""Creature-related ECS components."""
from dataclasses import dataclass
from enum import Enum, auto


class SpeciesType(Enum):
    """Types of species in the ecosystem."""

    # Basic creatures
    HERBIVORE = auto()
    CARNIVORE = auto()

    # Faction creatures
    SHROOMER = auto()  # Fungi-like, spreads via spores
    SECTID = auto()  # Insect-like, multiplies quickly
    FAELING = auto()  # Plant-like, grows crystals


@dataclass
class Species:
    """Defines what type of creature this entity is."""

    type: SpeciesType
    generation: int = 0


@dataclass
class Hunger:
    """Hunger/food need for an entity."""

    current: float
    max: float = 100.0
    decay_rate: float = 0.1
    starvation_damage: float = 1.0

    @property
    def percent(self) -> float:
        return self.current / self.max

    @property
    def is_starving(self) -> bool:
        return self.current <= 0


@dataclass
class Energy:
    """Energy/health for an entity."""

    current: float
    max: float = 100.0

    @property
    def percent(self) -> float:
        return self.current / self.max

    @property
    def is_dead(self) -> bool:
        return self.current <= 0


@dataclass
class Age:
    """Age tracking for natural death and maturity."""

    current: int = 0
    max_lifespan: int = 10000
    maturity_age: int = 1000

    @property
    def is_mature(self) -> bool:
        return self.current >= self.maturity_age

    @property
    def is_elderly(self) -> bool:
        return self.current >= self.max_lifespan * 0.8


@dataclass
class Reproduction:
    """Reproduction capability for an entity."""

    # Thresholds for reproduction
    hunger_threshold: float = 70.0  # Must have this much hunger to reproduce
    energy_threshold: float = 80.0  # Must have this much energy to reproduce

    # Costs of reproduction
    hunger_cost: float = 40.0  # Hunger lost when reproducing
    energy_cost: float = 30.0  # Energy lost when reproducing

    # Timing
    cooldown: int = 500  # Ticks between reproductions
    current_cooldown: int = 0

    # Offspring settings
    offspring_count: int = 1  # Number of offspring per reproduction
    spawn_radius: float = 3.0  # How far offspring spawn from parent
