# MVP Rapido: Simulador SMAW XR

## Estado actual del repo

- El proyecto ya tiene base XR en Unity con `OpenXR`, `AR Foundation`, `XR Interaction Toolkit` y soporte Meta/Quest en `Packages/manifest.json`.
- El codigo propio actual esta orientado a menu y transicion de escenas.
- No existe aun logica para:
  - leer sensores del Arduino,
  - trackear marcadores,
  - mapear la pinza real a un objeto virtual,
  - evaluar angulo, distancia, continuidad o consumo del electrodo,
  - anclar piezas fisicas al espacio MR.

## Objetivo realista de MVP

Construir una demo funcional en Quest 3 donde:

1. La pinza fisica se vea como pinza/electrodo virtual en MR.
2. Las piezas fisicas se alineen con sus modelos virtuales.
3. El sistema mida al menos:
   - angulo de trabajo,
   - distancia al objetivo,
   - continuidad,
   - tiempo,
   - consumo simulado del electrodo.
4. El usuario reciba una calificacion basica por ejercicio.

## Arquitectura recomendada

### 1. Tracking espacial

- `Headset / Quest 3`: posicion absoluta del usuario y rendering MR.
- `Marcador en la pinza`: da posicion y rotacion global de la herramienta.
- `Marcador en cada pieza o base de pieza`: da anclaje del modelo soldable.
- `IMU MPU6050 en la pinza`: se usa como apoyo para suavizar orientacion o para metricas, no como fuente principal de posicion.
- `Sensor laser`: mide longitud de arco / distancia a la pieza.
- `Servo + cremallera`: representa desgaste del electrodo y se sincroniza con la simulacion.

### 2. Comunicacion de hardware

- Arduino lee sensores y emite un paquete simple.
- Unity recibe el paquete, lo filtra y actualiza estado del ejercicio.
- El tracking visual del marcador y los sensores deben fusionarse en Unity.

### 3. Evaluacion

- Un `WeldSessionManager` arranca y termina la practica.
- Un `WeldMetricsCalculator` calcula:
  - error angular,
  - error de distancia,
  - porcentaje de continuidad,
  - tiempo total,
  - avance util,
  - consumo estimado.
- Un `WeldExerciseDefinition` define la pieza, tolerancias y meta del ejercicio.

## Como comunicar Arduino con Quest

### Opcion recomendada para MVP: Arduino -> PC puente -> Quest por Wi-Fi

Flujo:

- Arduino Uno por USB serial a un PC.
- Aplicacion puente en PC lee serial.
- El puente envia UDP o WebSocket a Unity en Quest por la red local.

Ventajas:

- Mucho mas rapido de implementar.
- Facil de depurar.
- No depende de plugins Android USB o BLE desde el primer dia.
- Permite registrar telemetria facilmente.

Desventaja:

- Requiere PC encendido durante la demo.

### Opcion 2: Arduino -> modulo inalambrico -> Quest directo

Variantes:

- `ESP32` como reemplazo o coprocesador del Uno.
- `HC-05/HC-06` clasico Bluetooth serial.
- `BLE` con un modulo compatible.

Recomendacion:

- Si quieren producto mas limpio y portable, migren pronto a `ESP32`.
- Si se quedan con `Arduino Uno` puro, la conectividad directa con Quest es bastante mas incomoda.

### Opcion 3: Arduino -> USB OTG -> Quest

Es posible en teoria con Android USB host, pero para MVP no la recomiendo porque:

- exige plugin nativo o libreria Android,
- complica permisos,
- hace mas lenta la iteracion,
- agrega riesgo justo en la capa de integracion.

## Como reconocer las piezas para realidad mixta

### Recomendacion MVP: marcadores visuales

Usar un marcador en:

- la base de la pinza,
- la base o soporte de cada pieza.

Dos enfoques viables:

1. `Image tracking` con imagenes de referencia.
2. `Fiduciales tipo ArUco/AprilTag` procesados por vision.

### Cual elegir

Para MVP rapido:

- Si el stack de Quest 3 + Unity les responde bien con `tracked images`, empiecen por ahi.
- Si necesitan mas robustez de pose y mas control del marcador, `AprilTag/Aruco` suele dar mejor comportamiento, pero requiere pipeline de vision.

### Recomendacion practica

- Marcador grande y de alto contraste.
- Separar el marcador de la zona de trabajo para que no quede tapado por la mano.
- En piezas pequenas, usar un soporte/base comun con marcador fijo y luego aplicar un offset conocido hacia la zona de soldadura.

## Como mapear la pinza real al electrodo virtual

No recomiendo usar solo el giroscopio para la pose completa.

Pipeline recomendado:

1. El marcador de la pinza da pose global.
2. La IMU ayuda a suavizar rotacion y detectar micro movimientos.
3. El laser da distancia real a la superficie.
4. Unity aplica un offset calibrado entre marcador, mango y punta del electrodo.
5. El servo informa o recibe el valor de longitud consumida.

Formula mental:

`Pose punta virtual = Pose marcador pinza + offset calibrado + correccion de longitud de electrodo`

## Fusion de datos recomendada

- Vision = posicion absoluta y orientacion base.
- IMU = orientacion relativa y suavizado.
- Laser = distancia de trabajo.
- Servo = longitud del electrodo disponible.

No hagan integracion de posicion con MPU6050 para el MVP.
La deriva seria demasiado alta.

## Backlog tecnico sugerido en Unity

### Fase 1. Base MR

- Crear escena nueva para entrenamiento MR.
- Configurar `XROrigin`, passthrough / camara MR y UI minima.
- Crear sistema de calibracion inicial.

### Fase 2. Tracking

- Implementar `MarkerAnchor` para pinza y piezas.
- Crear offsets calibrables por inspector.
- Mostrar gizmos / debug visual.

### Fase 3. Sensores

- Crear `ArduinoBridgeReceiver`.
- Definir payload JSON o CSV:
  - timestamp,
  - imu pitch/roll/yaw,
  - laser_mm,
  - servo_mm,
  - botones/estado.
- Filtrar datos con suavizado simple.

### Fase 4. Logica de soldadura

- Detectar inicio/fin de cordon.
- Calcular desviacion angular.
- Calcular rango valido de distancia.
- Medir continuidad y pausas.
- Asociar reglas por pieza.

### Fase 5. Feedback

- Chispa/arco visual.
- Color verde/amarillo/rojo por calidad.
- Puntaje final por ejercicio.

## Complejidad real

### Complejidad global

- `Media-Alta` para un MVP funcional.
- `Alta` si quieren hacerlo robusto, sin PC puente y con tracking fino.

### Por modulo

- Base Unity MR en Quest 3: `Media`
- Tracking con marcadores: `Media-Alta`
- Comunicacion Arduino-Quest: `Media` con puente, `Alta` directo
- Fusion de sensores: `Media`
- Metricas SMAW basicas: `Media`
- Realismo visual/haptico avanzado: `Alta`

## Riesgos principales

- Oclusion del marcador por la mano o por la pieza.
- Latencia o perdida de paquetes si no se filtra bien.
- Drift o ruido del MPU6050.
- Laser con lecturas erraticas por angulo/material/superficie.
- Mala calibracion entre marcador y punta real del electrodo.
- Intentar medir demasiadas variables antes de estabilizar pose y distancia.

## Alcance recomendado de primera demo

Primera demo exitosa:

- Solo 1 pieza: `Tope en U`.
- 1 marcador en la pinza.
- 1 marcador en la base de la pieza.
- 3 metricas:
  - angulo,
  - distancia,
  - continuidad.
- Consumo de electrodo solo visual y lineal.
- Comunicacion via PC puente.

## Lo que haria yo en orden

1. Congelar el alcance en una sola pieza.
2. Implementar tracking por marcadores antes de tocar evaluacion avanzada.
3. Meter puente serial -> UDP/WebSocket.
4. Llevar laser y servo primero; dejar IMU como apoyo.
5. Construir calibracion de offsets.
6. Implementar scoring basico.
7. Probar con usuarios.

## Decision tecnica recomendada

Si el objetivo es "MVP rapido":

- `Quest 3 + Unity MR`
- `Marcadores para pinza y piezas`
- `PC puente para serial`
- `Laser como sensor principal de calidad`
- `IMU solo como apoyo`
- `Una sola pieza y una sola rubrica al inicio`

Si el objetivo cambia a "producto mas portable":

- Migrar de `Arduino Uno` a `ESP32`
- Reducir dependencia del PC
- Mejorar vision y calibracion
- Explorar haptica y scoring mas fino
