"""Simulation control components."""
from dataclasses import dataclass


@dataclass
class SimulationLOD:
    """Level of Detail for simulation - determines update frequency."""

    level: int = 0  # 0=full, 1=reduced, 2=statistical, 3=aggregate
    ticks_until_update: int = 0  # Countdown to next update

    def should_update(self) -> bool:
        """Return True if entity should be updated this tick."""
        return self.ticks_until_update <= 0

    def tick(self) -> None:
        """Decrement the update counter."""
        if self.ticks_until_update > 0:
            self.ticks_until_update -= 1
