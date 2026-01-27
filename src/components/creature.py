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
