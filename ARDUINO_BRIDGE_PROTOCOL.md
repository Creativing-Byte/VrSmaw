# Arduino Bridge Protocol

La telemetria base del proyecto queda definida desde Arduino como `CSV por Serial USB`, pero la app final en Quest recibe esos datos por `UDP` desde la PC puente.

## Formato recomendado

CSV en este orden:

```text
timestampMs,laserMm,consumedMm,servo01,pitchDeg,rollDeg,yawDeg,triggerPressed,trackingConfidence,electrodeBtn
```

Ejemplo:

```text
1715001234567,3.4,12.0,0.22,44.8,2.1,180.0,1,0.95,2
```

## Campos

- `timestampMs`: tiempo de la lectura.
- `laserMm`: distancia medida por el laser a la pieza.
- `consumedMm`: cuantos milimetros de electrodo ya se consumieron.
- `servo01`: posicion normalizada del servo entre `0` y `1`.
- `pitchDeg`, `rollDeg`, `yawDeg`: orientacion del sensor IMU.
- `triggerPressed`: `1` o `0` para indicar sesion activa.
- `trackingConfidence`: confianza opcional entre `0` y `1`.
- `electrodeBtn`: boton fisico de seleccion de electrodo (momento de pulsacion).
  - `0` = ningun boton presionado (valor por defecto entre pulsaciones)
  - `1` = E6013 3/32" (2.4 mm)
  - `2` = E6013 1/8"  (3.2 mm)
  - `3` = E6013 5/32" (4.0 mm)
  > El Arduino debe enviar el valor del boton solo durante 1-2 frames y luego volver a `0`.

## JSON compatible

Tambien se aceptan cargas tipo JSON con claves equivalentes:

```json
{
  "timestampMs": 1715001234567,
  "laserMm": 3.4,
  "consumedMm": 12.0,
  "servo01": 0.22,
  "pitch": 44.8,
  "roll": 2.1,
  "yaw": 180.0,
  "active": true,
  "trackingConfidence": 0.95,
  "electrodeBtn": 2
}
```

## Flujo final recomendado

1. Arduino envia serial por USB a la PC.
2. La escena [PCArduinoBridge.unity](/Users/usuario/Desktop/DigitalSmaw/Assets/Scenes/PCArduinoBridge.unity) lee el puerto `COM` / `tty`.
3. `ArduinoTelemetryUdpForwarder` reenvia los datos por `UDP` a la IP de las Quest.
4. La escena [SampleScene.unity](/Users/usuario/Desktop/DigitalSmaw/Assets/Scenes/SampleScene.unity) recibe esa telemetria en el puerto `9100`.

## Carpeta de firmware

El firmware base del `Arduino Uno` y el pinout recomendado quedaron en:

- [Hardware/ArduinoUno_SMAW/README.md](/Users/usuario/Desktop/DigitalSmaw/Hardware/ArduinoUno_SMAW/README.md)
- [Hardware/ArduinoUno_SMAW/ArduinoUno_SMAW.ino](/Users/usuario/Desktop/DigitalSmaw/Hardware/ArduinoUno_SMAW/ArduinoUno_SMAW.ino)

## Notas

- Si la pinza ya esta en `SampleScene`, el script `MigWelding` intentara encontrar o crear automaticamente un `ArduinoBridgeReceiver`.
- El nombre `MigWelding` se mantuvo por compatibilidad con el prefab existente, aunque la logica nueva esta orientada al simulador SMAW.
