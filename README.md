# DigitalSmaw

Simulador MVP de entrenamiento SMAW en Unity para `Meta Quest 3`, con:

- tracking de pinza y piezas mediante `AprilTag` vistos por las cámaras del visor
- telemetría física desde `Arduino Uno`
- puente `PC -> Quest` por red local
- evaluación guiada por ejercicio

Este repositorio ya no está pensado como una app puramente `PCVR`. La arquitectura activa es:

1. `Quest 3 standalone` ejecuta la app de realidad mixta y hace el tracking visual.
2. `PC` recibe el `Arduino Uno` por `USB/COM`.
3. `PC` reenvía la telemetría a las gafas por `UDP`.

---

## 1. Qué hace hoy la app

La versión actual permite:

- iniciar desde una escena de menú
- entrar a una escena de entrenamiento MR
- seleccionar electrodo y ejercicio antes de comenzar
- usar botones físicos del Arduino para cambiar de electrodo
- seguir la pinza con una board de `AprilTag`
- seguir piezas físicas con tags dedicados
- leer:
  - distancia láser
  - consumo del electrodo
  - orientación IMU
  - trigger de sesión
  - botones de selección de electrodo
- correr una secuencia guiada de `5 ejercicios`
- cerrar cada ejercicio automáticamente y avanzar al siguiente
- mostrar resultados y puntaje al finalizar cada tramo

---

## 2. Arquitectura del sistema

### Quest 3

Responsabilidades:

- render MR
- acceso a cámaras del visor
- detección de `AprilTag`
- cálculo de pose de pinza y piezas
- recepción de telemetría Arduino por `UDP`
- evaluación de la sesión

Escena principal:

- `Assets/Scenes/SampleScene.unity`

### PC

Responsabilidades:

- leer el `COM` del Arduino
- reenviar telemetría a Quest

Opciones actuales:

1. Companion en Unity:
   - `Assets/Scenes/PCArduinoBridge.unity`
2. Relay de consola en `.NET`:
   - `Tools/SmawSerialRelay`

### Arduino Uno

Responsabilidades:

- leer `MPU6050`
- leer `VL53L0X`
- leer botones físicos
- mover el mecanismo del electrodo
- emitir telemetría por `Serial USB`

Firmware:

- `Hardware/ArduinoUno_SMAW/ArduinoUno_SMAW.ino`

---

## 3. Escenas y flujo de uso

Las escenas activas en `Build Settings` son:

1. `Assets/Scenes/1 Start Scene.unity`
2. `Assets/Scenes/SampleScene.unity`

Flujo esperado:

1. El usuario abre la app en Quest.
2. Ve el menú principal.
3. Pulsa `Start`.
4. Entra a `SampleScene`.
5. Aparece el menú de selección de electrodo y ejercicio.
6. Puede elegir electrodo desde UI o con botones físicos del Arduino.
7. Inicia la secuencia guiada.
8. La app evalúa el ejercicio, lo cierra automáticamente y avanza al siguiente.

---

## 4. Hardware mínimo

### Visor

- `Meta Quest 3`
- modo desarrollador activado
- misma red `Wi‑Fi` que la PC

### PC

- Unity instalado para compilar la app Quest
- `dotnet 8` si se va a usar `Tools/SmawSerialRelay`
- mismo segmento de red que Quest

### Arduino

- `Arduino Uno`
- `MPU6050`
- `VL53L0X`
- `servo` o mecanismo equivalente para consumo del electrodo
- `3 botones` para selección física de electrodo
- `1 botón` de trigger/sesión

### Conexión recomendada

- `Arduino Uno -> USB -> PC`
- `PC -> Wi‑Fi local -> Quest 3`

### Puertos del Arduino

Pinout actual esperado por el firmware:

- `D2`
  - botón físico electrodo `1`
- `D3`
  - botón físico electrodo `2`
- `D4`
  - botón físico electrodo `3`
- `D5`
  - botón de `trigger` / sesión activa
- `D9`
  - señal del motor / servo que mueve la cremallera
- `A4`
  - `SDA` del bus `I2C`
- `A5`
  - `SCL` del bus `I2C`

Sensores conectados sobre `I2C`:

- `MPU6050`
  - `SDA -> A4`
  - `SCL -> A5`
  - `VCC -> 5V`
  - `GND -> GND`
- `VL53L0X`
  - `SDA -> A4`
  - `SCL -> A5`
  - `VCC -> 5V` o `3.3V` según el módulo
  - `GND -> GND`

Motor / servo de la cremallera:

- señal de control:
  - `servo -> D9`
- alimentación:
  - usar `5V` externa
  - unir `GND` de la fuente externa con `GND` del Arduino

Importante:

- no alimentar el servo directamente desde el `5V` del Arduino si la cremallera tiene carga real
- el `MPU6050` y el `VL53L0X` comparten el mismo bus `I2C`

---

## 5. Cómo iniciar el sistema

### Opción recomendada para pruebas reales

Usar:

- app principal en Quest
- relay de consola en PC

### Paso 1. Conectar Arduino

1. Conecta el `Arduino Uno` por USB a la PC.
2. Carga el firmware:
   - `Hardware/ArduinoUno_SMAW/ArduinoUno_SMAW.ino`
3. Verifica que el Arduino emite datos por serial a `115200`.

Formato esperado:

```text
timestampMs,laserMm,consumedMm,servo01,pitchDeg,rollDeg,yawDeg,triggerPressed,trackingConfidence,electrodeBtn
```

### Paso 2. Lanzar el relay de PC

Desde la raíz del repo:

```bash
dotnet run --project Tools/SmawSerialRelay/SmawSerialRelay.csproj
```

Opcionalmente puedes fijar el puerto:

```bash
dotnet run --project Tools/SmawSerialRelay/SmawSerialRelay.csproj -- --port COM5 --baud 115200
```

Qué hace este relay:

- autodetecta el puerto serial
- escucha beacons de Quest en `UDP 9101`
- descubre la IP de las gafas
- reenvía la telemetría al puerto `9100` de Quest

### Paso 3. Lanzar la app en Quest

1. Abre el proyecto en Unity.
2. Cambia la plataforma a `Android`.
3. Conecta las Quest por `USB-C` solo para instalación/debug.
4. Haz `Build And Run` sobre:
   - `Assets/Scenes/1 Start Scene.unity`
   - `Assets/Scenes/SampleScene.unity`

Importante:

- la app debe ejecutarse `standalone` en Quest
- no usar `Quest Link` como modo principal de operación para esta versión
- el cable USB se usa para instalar y depurar, no para la experiencia final

Una vez instalada:

1. Ejecuta la app directamente en las gafas.
2. Asegúrate de que PC y Quest están en la misma red.
3. La Quest enviará un beacon por `UDP 9101`.
4. El relay de PC detectará ese beacon y empezará a reenviar telemetría.

### Paso 4. Iniciar la práctica

1. En el visor, entra al menú principal y pulsa `Start`.
2. Espera a que aparezca el panel de selección.
3. Elige electrodo y ejercicio.
4. Presenta la pinza y las piezas con sus tags visibles.
5. Pulsa `INICIAR`.

---

## 6. Alternativa: companion de PC en Unity

Si no quieres usar el relay `.NET`, puedes usar la escena:

- `Assets/Scenes/PCArduinoBridge.unity`

Configuración recomendada:

- `Arduino Bridge`
  - `transportMode = SerialUsb`
  - `serialBaudRate = 115200`
  - `autoDetectSerialPort = true`
- `Quest Telemetry Forwarder`
  - `targetHost = IP de las Quest`
  - `targetPort = 9100`

Esta opción sirve para depuración rápida desde el editor, pero para operación continua el relay de consola suele ser más cómodo.

---

## 7. Marcadores y tracking

La librería instalada usa:

- `TagStandard41h12`

No imprimir `36h11` para esta versión.

### IDs recomendados

- `10` y `11`: board física de la pinza
- `100`: marcador virtual fusionado de la pinza
- `1`: pieza 1
- `2`: pieza 2
- `3`: pieza 3
- `4`: cilindro

### Objetos ya conectados a tracking

- `Mig`
  - sigue `markerId = 100`
- `Cylinder`
  - sigue `markerId = 4`

La board de la pinza se fusiona con:

- `AprilTagBoardTracker`

### Recomendación física

- pinza:
  - board rígida
  - dos tags en el mismo plano
  - separación de `8 a 12 cm`
  - lejos de la mano y de la punta
- piezas:
  - poner el tag en la base o soporte
  - no en la cara exacta de soldadura

Tamaños recomendados:

- pinza: `6 a 8 cm`
- piezas: `8 a 12 cm`

---

## 8. Conexión Arduino -> Unity -> Quest

### Protocolo actual

El firmware emite telemetría por `Serial USB`.

La PC la recibe y la reenvía a Quest por `UDP`.

Puertos usados:

- `9100`: telemetría Arduino hacia Quest
- `9101`: beacon de Quest hacia PC
- `9200`: reservado para poses externas de AprilTag si se quieren inyectar

### Sensores usados por la simulación

- `laserMm`
  - distancia real de trabajo
- `consumedMm`
  - consumo acumulado del electrodo
- `servo01`
  - estado normalizado del mecanismo
- `pitchDeg`, `rollDeg`, `yawDeg`
  - orientación IMU
- `triggerPressed`
  - sesión activa
- `electrodeBtn`
  - selección física de electrodo

### Importante sobre la cremallera

El firmware ya incluye retorno físico de la cremallera al cambiar electrodo.

Comportamiento:

1. se solicita cambio de electrodo
2. el sistema manda el rack a `home`
3. bloquea consumo mientras vuelve
4. al terminar reinicia la referencia lógica

Esto funciona en modo `SERVO_MODE_CONTINUOUS_ESTIMATED`, pero sigue siendo una solución de lazo abierto.

Para una versión más robusta se recomienda agregar:

- `endstop` o sensor de `home`
- o un actuador con feedback real de posición

---

## 9. Ejercicios disponibles

La app trabaja hoy con `5 ejercicios`:

1. `P1 – Cordón plano`
2. `P2 – Unión en T`
3. `P3 – Cuña (13°)`
4. `P4 – Ranura en V`
5. `P5 – Cilindro 360°`

La secuencia guiada los encadena automáticamente.

---

## 10. Fundamentos de la calificación

La calificación no intenta simular metalurgia real completa. En esta versión evalúa lo que sí se puede medir de forma consistente con el hardware disponible.

### Variables base usadas por la evaluación

- orientación de la herramienta
- distancia a la pieza
- continuidad del arco
- tiempo activo de trabajo
- cobertura o recorrido útil
- cierre del ejercicio
- estabilidad del arco

### Criterios implementados

La app tiene `14 criterios` y activa solo los que corresponden a cada ejercicio:

1. Rectitud del cordón
2. Uniformidad de altura
3. Ángulo de trabajo 45°
4. Adaptación a ángulo 13°
5. Uniformidad en cuña
6. Continuidad por pausas
7. Interrupciones del cordón
8. Cierre circunferencial 360°
9. Ángulo de trabajo en curva
10. Continuidad y cierre por láser
11. Control del arco
12. Posición del cordón
13. Cobertura del cordón
14. Velocidad de avance

### Qué evalúa cada ejercicio

- `P1`
  - rectitud
  - uniformidad
- `P2`
  - rectitud geométrica
  - ángulo de trabajo a `45°`
  - continuidad
  - posición del cordón
  - cobertura
  - velocidad de avance
- `P3`
  - adaptación a `13°`
  - uniformidad
  - continuidad
- `P4`
  - continuidad e interrupciones
- `P5`
  - cierre `360°`
  - estabilidad angular en curva
  - cierre por láser
  - control del arco

### Cómo se calcula el puntaje

- cada criterio aplicable se puntúa de `0` a `100`
- el puntaje final es el promedio de los criterios activos del ejercicio
- la sesión guiada usa progreso mínimo y pausa final para decidir cuándo cerrar automáticamente

### Cómo termina automáticamente un ejercicio

La lógica guiada cierra un ejercicio cuando:

1. ya hubo trabajo real de arco
2. el progreso útil llega a aproximadamente `90%`
3. el usuario deja una pausa final suficiente

Casos especiales:

- `P2`
  - el progreso se mide por cobertura efectiva del seam
- `P5`
  - mezcla tiempo de arco, rotación acumulada y cierre por láser

---

## 11. Apartado técnico

### Scripts principales

- `Assets/Scripts/ArduinoBridgeReceiver.cs`
  - recibe serial o UDP y parsea telemetría
- `Assets/Scripts/MigWelding.cs`
  - representa la pinza y el arco en escena
- `Assets/Scripts/WeldingSelectionMenu.cs`
  - selección de electrodo y ejercicio
- `Assets/Scripts/WeldingEvaluator.cs`
  - scoring y métricas
- `Assets/Scripts/WeldingTrainingFlowController.cs`
  - secuencia guiada y autoavance
- `Assets/Scripts/WeldingScoreUI.cs`
  - HUD y panel de resultados
- `Assets/Scripts/QuestAprilTagTrackingPipeline.cs`
  - pipeline de detección de tags en Quest
- `Assets/Scripts/AprilTagBoardTracker.cs`
  - fusiona la board de la pinza
- `Assets/Scripts/AprilTagTrackedObject.cs`
  - aplica la pose a objetos de escena
- `Assets/Scripts/QuestBeaconSender.cs`
  - anuncia la Quest a la PC para autodetección

### Flujo de datos

```text
Arduino Uno
  -> Serial USB
PC relay / PC bridge
  -> UDP 9100
Quest app
  -> ArduinoBridgeReceiver
  -> MigWelding / WeldingEvaluator / UI
```

### Flujo de tracking

```text
Cámaras Quest
  -> QuestPassthroughCameraSource
  -> QuestAprilTagTrackingPipeline
  -> AprilTagBoardTracker
  -> AprilTagTrackedObject
  -> pinza y piezas virtuales
```

---

## 12. Limitaciones actuales

- el `yaw` del `MPU6050` deriva con el tiempo
- el retorno del rack sigue siendo estimado si no hay sensor de `home`
- la experiencia depende de buena visibilidad de los tags
- la evaluación es de entrenamiento geométrico y procedimental, no de calidad metalúrgica real

---

## 13. Archivos útiles del repo

- `Assets/Scenes/1 Start Scene.unity`
- `Assets/Scenes/SampleScene.unity`
- `Assets/Scenes/PCArduinoBridge.unity`
- `Hardware/ArduinoUno_SMAW/ArduinoUno_SMAW.ino`
- `Hardware/ArduinoUno_SMAW/README.md`
- `Hardware/ArduinoUno_SMAW/SERIAL_PROTOCOL.md`
- `PC_QUEST_ARDUINO_SETUP.md`
- `QUEST_APRILTAG_SETUP.md`
- `ARDUINO_BRIDGE_PROTOCOL.md`

---

## 14. Siguiente mejora recomendada

Si se quiere estabilizar la versión para uso repetido en laboratorio, las prioridades serían:

1. agregar sensor de `home` al mecanismo de la cremallera
2. fijar un flujo formal de calibración de offsets
3. registrar resultados en archivo o base de datos
4. ajustar umbrales de scoring con pruebas reales de instructores
