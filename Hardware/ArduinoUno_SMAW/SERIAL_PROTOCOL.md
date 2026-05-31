# Serial Protocol

## Puerto y velocidad

- `USB Serial`
- `115200` baudios
- `8N1`
- una linea por muestra

## Formato CSV

Orden fijo:

```text
timestampMs,laserMm,consumedMm,servo01,pitchDeg,rollDeg,yawDeg,triggerPressed,trackingConfidence,electrodeBtn
```

## Campos

1. `timestampMs`
   Tiempo Arduino en milisegundos.
2. `laserMm`
   Distancia medida por el laser en milimetros.
3. `consumedMm`
   Milimetros consumidos del electrodo virtual.
4. `servo01`
   Posicion normalizada del mecanismo de consumo entre `0.0` y `1.0`.
5. `pitchDeg`
   Pitch estimado por IMU.
6. `rollDeg`
   Roll estimado por IMU.
7. `yawDeg`
   Yaw integrado por giroscopio.
8. `triggerPressed`
   `1` si la sesion esta activa, `0` si no.
9. `trackingConfidence`
   Confianza simple entre `0.0` y `1.0`.
10. `electrodeBtn`
    Pulso momentaneo de boton fisico:
    - `0` = ninguno
    - `1` = electrodo `3/32`
    - `2` = electrodo `1/8`
    - `3` = electrodo `5/32`

## Frecuencia sugerida

- Telemetria: `30 Hz`
- Laser: `20-30 Hz`
- IMU: cada loop

## Ejemplo realista

```text
15230,3.84,11.52,0.046,14.28,-1.74,27.16,1,1.00,0
```

## Comportamiento esperado de botones fisicos

Para que Unity detecte bien el cambio de electrodo:

- enviar `1`, `2` o `3` solo durante `1-2` frames
- despues volver a `0`

Ejemplo:

```text
20110,0.00,0.00,0.000,0.00,0.00,0.00,0,1.00,2
20143,0.00,0.00,0.000,0.00,0.00,0.00,0,1.00,0
```
