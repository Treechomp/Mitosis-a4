"""Hunger system for entity metabolism."""
import esper

from ...components.ecosystem import Hunger, Energy


class HungerProcessor(esper.Processor):
    """Processes hunger decay and starvation damage."""

    def process(self) -> None:
        """Update hunger for all entities."""
        entities_to_kill = []

        for entity, (hunger,) in self.world.get_component(Hunger):
            # Decrease hunger over time
            hunger.current -= hunger.decay_rate

            # Clamp to 0
            if hunger.current < 0:
                hunger.current = 0

                # Apply starvation damage if entity has energy
                if self.world.has_component(entity, Energy):
                    energy = self.world.component_for_entity(entity, Energy)
                    energy.current -= hunger.starvation_damage

                    # Mark for death if no energy
                    if energy.is_dead:
                        entities_to_kill.append(entity)

        # Remove dead entities
        for entity in entities_to_kill:
            self.world.delete_entity(entity)
