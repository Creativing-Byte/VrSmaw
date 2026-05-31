# Quest AprilTag Setup

## Estado actual

La escena [SampleScene.unity](/Users/usuario/Desktop/DigitalSmaw/Assets/Scenes/SampleScene.unity) ya quedo preparada para el lado Quest:

- `AprilTag System`
  - `QuestCameraPermissionGate`
  - `QuestPassthroughCameraSource`
  - `QuestAprilTagTrackingPipeline`
  - `AprilTagTrackingReceiver`
  - `AprilTagBoardTracker`
- `Mig`
  - `AprilTagTrackedObject` con `markerId = 100`
- `Cylinder`
  - `AprilTagTrackedObject` con `markerId = 4`
- `Arduino Bridge`
  - `ArduinoBridgeReceiver` en modo `UDP` puerto `9100`

## Arquitectura final

1. Las Quest 3 detectan la pinza y las piezas usando sus propias camaras.
2. `QuestAprilTagTrackingPipeline` estima la pose de cada tag.
3. `AprilTagBoardTracker` fusiona varios tags fisicos de la pinza en un solo marcador virtual mas estable.
4. `AprilTagTrackedObject` mueve la pinza y las piezas virtuales.
5. La PC recibe el Arduino por `USB/COM` y reenvia la telemetria por `UDP` a Quest.

## Familia de marcadores

La libreria instalada en este proyecto es `jp.keijiro.apriltag` y esta compilada para:

- `TagStandard41h12`

Eso significa que para este proyecto deben imprimir `TagStandard41h12`, no `36h11`.

## Asignacion de IDs recomendada

- `10` y `11`: tags fisicos de la board de la pinza
- `100`: marcador virtual fusionado de la pinza
- `1`: figura 1
- `2`: figura 2
- `3`: figura 3
- `4`: cilindro

## Board de la pinza

La pinza no sigue un solo tag. Ahora usa una board virtual:

- objeto: `AprilTagBoardTracker`
- tags fuente por defecto: `10` y `11`
- salida fusionada: `markerId = 100`

Recomendacion fisica:

- montar una placa rigida en la pinza
- separar los dos tags `8` a `12 cm` entre centros
- ubicar la board fuera de la mano y lejos de la punta del electrodo
- mantener ambos tags en el mismo plano

## Tamano recomendado

- pinza: tags de `6` a `8 cm`
- piezas: tags de `8` a `12 cm`

El `tagSizeMeters` actual del pipeline esta en `0.08`, asi que si imprimen a otro tamano deben actualizar ese valor.

## Flujo de calibracion inicial

1. Abrir `SampleScene`.
2. Ejecutar en Quest standalone.
3. Verificar que `QuestPassthroughCameraSource` detecte un dispositivo de camara.
4. Presentar la board de la pinza a las camaras.
5. Ajustar `localPositionOffset` y `localEulerOffset` de `AprilTagTrackedObject` en `Mig`.
6. Ajustar los offsets de cada pieza si hace falta.

## Modo de prueba

`QuestAprilTagTrackingPipeline` sigue teniendo `simulateMarkerFromTransform`.

Sirve para:

- validar el movimiento de `Mig` sin tags impresos
- probar offsets
- revisar el suavizado

## Referencias oficiales

- [Meta Passthrough Camera API Overview](https://developers.meta.com/horizon/documentation/unity/unity-pca-overview/)
