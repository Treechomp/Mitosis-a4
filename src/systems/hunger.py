"""Hunger system for entity metabolism."""
import esper

from components import Hunger, Energy


class HungerProcessor(esper.Processor):
    """Processes hunger decay and starvation damage."""

    def process(self) -> None:
        """Update hunger for all entities."""
        entities_to_kill = []

        for entity, (hunger,) in esper.get_component(Hunger):
            hunger.current -= hunger.decay_rate

            if hunger.current < 0:
                hunger.current = 0

                if esper.has_component(entity, Energy):
                    energy = esper.component_for_entity(entity, Energy)
                    energy.current -= hunger.starvation_damage

                    if energy.is_dead:
                        entities_to_kill.append(entity)

        for entity in entities_to_kill:
            esper.delete_entity(entity)
