# Mitosis - Project Documentation

## Project Overview

**Mitosis** is a top-down, real-time RPG/strategy game featuring procedurally generated worlds and dynamic ecosystem simulation. Players explore vast landscapes populated by various entities that interact with each other and the environment in complex ways.

### Core Vision
- Procedurally generated worlds with diverse biomes and terrain features
- Dynamic ecosystem simulation with entity interactions
- Real-time strategy elements with RPG progression
- Environmental storytelling through ecosystem dynamics

---

## Technical Architecture

### Core Systems Architecture
```
┌─────────────────────────────────────────────────────────────┐
│                        Main Game Loop                        │
│  (main.py → game.py)                                        │
└─────────────────┬───────────────────────────────────────────┘
                  │
         ┌────────▼────────┐
         │   Game Manager   │
         │    (game.py)     │
         └─┬─────────────┬─┘
           │             │
    ┌──────▼──────┐   ┌──▼──────────┐
    │   World     │   │   Player    │
    │ (world.py)  │   │ (player.py) │
    └──────┬──────┘   └─────────────┘
           │
    ┌──────▼──────┐
    │  Entities   │
    │ (entity.py) │
    └─────────────┘

         Graphics Layer
    ┌─────────────────────┐
    │  Graphics System    │
    │   (graphics.py)     │
    └─────────┬───────────┘
              │
    ┌─────────▼───────────┐
    │   Renderer System   │
    │   (renderer.py)     │
    └─────────────────────┘
```

### File Structure
- **main.py**: Entry point, initializes game
- **game.py**: Main game loop, central coordination, entity management
- **world.py**: World generation, terrain management, tile systems
- **player.py**: Player character logic and movement
- **entity.py**: All entity classes, behaviors, and AI systems
- **graphics.py**: Graphics resource management and caching
- **renderer.py**: Rendering pipeline and camera management

---

## Currently Implemented Features

### 🌍 World Generation System

#### **Noise-Based Terrain Generation**
- **Multiple Noise Layers**: Elevation, moisture, and temperature maps
- **Biome Classification**: 24 different tile types based on environmental parameters
- **Terrain Types**:
  - Water: Deep Water, Shallow Water, Ice, Reef
  - Ground: Beach, Dune, Dirt, Rocky Grass, Grass, Savanna, Tundra, Sand, Gravel
  - Wetlands: Swamp, Bog, Marsh
  - Forests: Deciduous, Coniferous, Dense Forest
  - Mountains: Rock, Scree, Snow, Glacier

#### **River Generation System**
- Water source detection in high elevation areas
- Downhill flow pathfinding following elevation gradients
- River carving with variable width based on flow distance
- Moisture influence around river systems
- Automatic termination at water bodies

#### **Terrain Processing**
- Multi-threaded world generation for large maps (1024x1024 tiles)
- Terrain smoothing with cliff preservation
- Moisture adjustment based on water proximity
- Progress tracking for generation process

### 🎮 Entity System

#### **Entity Types**
1. **Passive Static Entities**
   - Trees, rocks, and other harvestable resources
   - Don't move, provide resources when harvested
   - Health system with destruction mechanics

2. **Passive Moving Entities**
   - Animals that wander and flee from threats
   - Biome preferences and natural behaviors
   - Threat detection and escape responses

3. **Active Entities**
   - **Shroomers**: Splash damage attackers, moisture gatherers, spore spreaders
   - **Sectids**: Fast attackers, resource gatherers, multiplication mechanics
   - **Faelings**: Long-range attackers, tree planters, crystal spawners

#### **AI Behavior System**
- **State Machine**: Idle, Wander, Chase, Attack, Flee, Gather, Replant
- **Detection Systems**: Configurable perception ranges for each entity type
- **Combat Mechanics**: Health, damage, attack ranges, cooldowns
- **Special Abilities**:
  - Shroomer spore spreading and splash attacks
  - Sectid resource gathering and multiplication
  - Faeling replanting and crystal mechanics

#### **Entity Management**
- Chunk-based spawning/despawning system
- Entity density control based on biome types
- Performance optimization through active chunk management
- Entity pooling and lifecycle management

### 🎯 Player System

#### **Movement and Controls**
- Smooth 8-directional movement with diagonal normalization
- Terrain-based movement costs (water slows, mountains harder to traverse)
- Collision detection with world boundaries and obstacles
- Keyboard input handling (Arrow keys for movement, Z for zoom)

#### **Camera System**
- Smooth camera following player position
- Configurable zoom levels (normal and zoomed-out world view)
- Viewport culling for rendering optimization
- World boundary constraints

### 🎨 Graphics and Rendering

#### **Graphics Management**
- Centralized graphics resource system
- Color-coded tile and entity representation
- Health bar rendering for damaged entities
- Cached surface generation for performance

#### **Rendering Pipeline**
- Frustum culling (only render visible tiles/entities)
- Layered rendering: Tiles → Entities → Player → UI
- Debug information display (FPS, position, tile type)
- Efficient viewport calculations

#### **Performance Monitoring**
- Timing decorators for performance profiling
- Frame time tracking and FPS calculation
- Update time breakdowns (world, entities, player, camera)
- Entity count monitoring

### 🔧 Technical Features

#### **Chunk Management System**
- 32x32 tile chunks for entity management
- Active chunk tracking based on player position
- Configurable spawn/despawn radius
- Entity density balancing per chunk

#### **Inventory and Progression**
- Entity inventory systems with capacity limits
- Experience and leveling mechanics
- Attribute progression (Strength, Speed, Intelligence)
- Item dropping on entity death

#### **Game Settings**
- Configurable time scale for game speed
- FPS limiting and performance controls
- Entity population limits
- Biome-specific entity spawn rates

---

## Planned Features (Not Yet Implemented)

### 🚀 Phase 1: Performance Optimization

#### **Spatial Partitioning**
- Quadtree or spatial hash grid for entity queries
- Efficient nearby entity detection
- Reduced computational complexity for large entity counts

#### **Advanced Entity Management**
- Level of Detail (LOD) system for distant entities
- Entity pooling for common types
- Component-based architecture
- State-based update optimization

#### **Rendering Enhancements**
- Sprite batching system
- Dirty rectangle tracking
- Chunk surface caching
- Tile atlasing for GPU optimization

### 🗺️ Phase 2: Advanced World Generation

#### **Terrain Features**
- Ridge and valley generation algorithms
- Hydraulic erosion simulation
- Mountain range connectivity
- Realistic geological formations

#### **Landmark Generation**
- Unique geological features (canyons, mesas, volcanic regions)
- Connected mountain ranges with realistic elevation patterns
- Enhanced river systems with tributaries and deltas
- Lakes and inland seas with proper watersheds

#### **Biome Improvements**
- Gradient-based biome transitions
- Sub-biome and microclimate systems
- Seasonal variations
- Climate-based weather patterns

### 🎲 Phase 3: Gameplay Enhancement

#### **Complete Ecosystem**
- Full Shroomer lifecycle (spore spreading, growth, environmental adaptation)
- Sectid colony behaviors and resource competition
- Advanced Faeling abilities (possession, environmental manipulation)
- Predator-prey relationships and food webs

#### **Player Progression**
- Interactive inventory UI
- Crafting system with resource gathering
- Skill trees and character development
- Combat mechanics and equipment system

#### **User Interface**
- Comprehensive HUD with health, resources, and status
- Interactive minimap with fog of war
- Context-sensitive tooltips and information panels
- Settings and pause menu systems

#### **Save/Load System**
- World state serialization
- Player progress persistence
- Save file management interface
- Auto-save functionality

### 🎨 Phase 4: Visual Polish

#### **Sprite System**
- Pixel art assets for tiles and entities
- Animation framework with state machines
- Particle effects for combat and environmental interactions
- Visual feedback for all player actions

#### **Environmental Effects**
- Day/night cycle with lighting changes
- Weather systems (rain, snow, wind effects)
- Water animations and environmental ambiance
- Screen effects for combat and interactions

---

## Technical Specifications

### **World Parameters**
- **World Size**: 1024x1024 tiles (configurable)
- **Tile Size**: 32x32 pixels
- **Chunk Size**: 32x32 tiles
- **Entity Limit**: 200 (configurable)
- **Generation**: Multi-threaded using noise functions

### **Performance Targets**
- **Target FPS**: 60
- **Maximum Entities**: 1000+ (with optimization)
- **Memory Usage**: < 500MB
- **Load Time**: < 10 seconds

### **Dependencies**
- **pygame**: Graphics and input handling
- **noise**: Perlin noise generation
- **numpy**: Mathematical operations and array processing
- **multiprocessing**: Parallel world generation

### **System Requirements**
- **Python**: 3.8+
- **RAM**: 4GB minimum
- **CPU**: Multi-core recommended for world generation
- **Graphics**: Hardware acceleration supported

---

## Development Status

### **Completion Status**
- ✅ **Core Architecture**: Complete
- ✅ **Basic World Generation**: Complete
- ✅ **Entity System**: Core implementation complete
- ✅ **Player Movement**: Complete
- ✅ **Basic Rendering**: Complete
- 🔄 **Performance Optimization**: In progress
- ❌ **Advanced AI**: Planned
- ❌ **Visual Polish**: Planned
- ❌ **Save/Load System**: Planned

### **Known Issues**
1. Performance degradation with high entity counts
2. World generation lacks interesting terrain features
3. Entity behaviors are basic and need ecosystem completion
4. Visual feedback is minimal (placeholder graphics)
5. No save/load functionality

### **Immediate Priorities**
1. Implement spatial partitioning for entity performance
2. Move EntityManager to separate module
3. Add basic terrain features (ridges, valleys)
4. Optimize rendering pipeline

---

## Getting Started

### **Installation**
```bash
# Clone the repository
cd mitosis-project

# Install dependencies
pip install pygame noise numpy

# Run the game
python main.py
```

### **Controls**
- **Arrow Keys**: Move player
- **Z Key**: Toggle zoom (normal/world view)
- **ESC**: Exit game (via window close)

### **Development Setup**
1. Ensure Python 3.8+ is installed
2. Install required dependencies
3. Run `python main.py` to start the game
4. Use timing decorators to profile performance
5. Refer to roadmap document for development priorities

---

*Last Updated: January 2025*
*For technical issues or contributions, refer to the project roadmap and technical documentation.*