# PC Quest Arduino Setup

## Objetivo

Esta es la arquitectura final para cumplir los dos requisitos del proyecto:

- tracking de pinza y piezas con las camaras de las Quest
- Arduino Uno conectado por `USB` a la `PC`

## Reparto de responsabilidades

### Quest

- ejecuta la experiencia MR
- detecta `AprilTag`
- mueve pinza y piezas virtuales
- recibe telemetria del Arduino por `UDP`

### PC

- lee `COM` del Arduino Uno
- reenvia la telemetria a las Quest por red local

## Escenas

- [SampleScene.unity](/Users/usuario/Desktop/DigitalSmaw/Assets/Scenes/SampleScene.unity)
  - app principal Quest
- [PCArduinoBridge.unity](/Users/usuario/Desktop/DigitalSmaw/Assets/Scenes/PCArduinoBridge.unity)
  - companion de PC para `COM -> UDP`

## Como correr el companion de PC

1. Abrir `PCArduinoBridge.unity`
2. En `Arduino Bridge`, dejar:
   - `transportMode = SerialUsb`
   - `serialBaudRate = 115200`
   - `autoDetectSerialPort = true`
3. En `Quest Telemetry Forwarder`, poner la IP local de las Quest en `targetHost`
4. Ejecutar Play Mode o construir una build desktop dedicada

## Como corre la app Quest

1. Abrir `SampleScene.unity`
2. Confirmar que `Arduino Bridge` este en modo `UDP`
3. Construir para Android / Quest
4. Ejecutar en las gafas en la misma red local que la PC

## Puertos

- `9100`: telemetria Arduino desde PC hacia Quest
- `9200`: poses AprilTag externas si algun dia se quieren inyectar por red

## Observaciones

- la IP de las Quest puede cambiar segun la red Wi-Fi
- el port `9100` ya quedo configurado en la escena Quest
- si la autodeteccion del puerto falla en PC, fijar manualmente `serialPortName`
