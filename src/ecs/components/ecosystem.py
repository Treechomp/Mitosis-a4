"""Ecosystem-related ECS components."""
from dataclasses import dataclass
from enum import Enum, auto


class SpeciesType(Enum):
    """Types of species in the ecosystem."""

    # Passive creatures
    HERBIVORE = auto()  # Eats plants

    # Predators
    CARNIVORE = auto()  # Eats herbivores

    # Faction creatures (from original design)
    SHROOMER = auto()  # Fungi-like, spreads via spores
    SECTID = auto()  # Insect-like, multiplies quickly
    FAELING = auto()  # Plant-like, grows crystals


@dataclass
class Species:
    """Defines what type of creature this entity is."""

    type: SpeciesType
    generation: int = 0  # How many generations from original spawn


@dataclass
class Hunger:
    """Hunger/food need for an entity."""

    current: float  # Current hunger level (0 = starving, max = full)
    max: float = 100.0
    decay_rate: float = 0.1  # Hunger lost per tick
    starvation_damage: float = 1.0  # Damage taken when starving

    @property
    def percent(self) -> float:
        """Return hunger as percentage (0-1)."""
        return self.current / self.max

    @property
    def is_starving(self) -> bool:
        """Return True if entity is starving."""
        return self.current <= 0


@dataclass
class Energy:
    """Energy/health for an entity."""

    current: float
    max: float = 100.0

    @property
    def percent(self) -> float:
        """Return energy as percentage (0-1)."""
        return self.current / self.max

    @property
    def is_dead(self) -> bool:
        """Return True if entity has no energy (dead)."""
        return self.current <= 0


@dataclass
class Age:
    """Age tracking for natural death and maturity."""

    current: int = 0  # Age in ticks
    max_lifespan: int = 10000  # Die of old age
    maturity_age: int = 1000  # Can reproduce after this age

    @property
    def is_mature(self) -> bool:
        """Return True if entity can reproduce."""
        return self.current >= self.maturity_age

    @property
    def is_elderly(self) -> bool:
        """Return True if entity is past 80% of lifespan."""
        return self.current >= self.max_lifespan * 0.8
