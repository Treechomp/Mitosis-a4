class Entity:
    def __init__(self, x, y):
        self.x = x
        self.y = y

    def update(self):
        pass

    def render(self, screen):
        pass

class PassiveStatic(Entity):
    pass

class PassiveMoving(Entity):
    pass

class ActiveFaction(Entity):
    pass

class Shroomer(ActiveFaction):
    pass

class Sectid(ActiveFaction):
    pass

class Faeling(ActiveFaction):
    pass