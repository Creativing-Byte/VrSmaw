# Serial Protocol

## Puerto y velocidad

- `USB Serial`
- `115200` baudios
- `8N1`
- una línea por muestra

---

## Arduino → Unity  (telemetría de salida)

Formato CSV, orden fijo, una línea por frame a ~30 Hz:

```text
timestampMs,laserMm,consumedMm,servo01,pitchDeg,rollDeg,yawDeg,triggerPressed,trackingConfidence,electrodeBtn
```

### Campos

| # | Campo | Descripción |
|---|---|---|
| 1 | `timestampMs` | Tiempo Arduino en milisegundos |
| 2 | `laserMm` | Distancia al workpiece en mm (VL53L0X) |
| 3 | `consumedMm` | mm consumidos del electrodo (refleja lo que Unity envía) |
| 4 | `servo01` | Posición normalizada del rack [0.0 – 1.0] |
| 5 | `pitchDeg` | Pitch estimado por IMU (MPU6050) |
| 6 | `rollDeg` | Roll estimado por IMU |
| 7 | `yawDeg` | Yaw integrado desde giroscopio |
| 8 | `triggerPressed` | `1` soldando, `0` inactivo |
| 9 | `trackingConfidence` | 0.0 – 1.0 (0.5 IMU + 0.5 láser) |
| 10 | `electrodeBtn` | Pulso momentáneo: 0=ninguno 1=3/32 2=1/8 3=5/32 |

Ejemplo:
```text
15230,3.84,11.52,0.046,14.28,-1.74,27.16,1,1.00,0
```

---

## Unity → Arduino  (comandos de entrada)

Unity es la **fuente de verdad** del consumo del electrodo.
El Arduino recibe comandos ASCII por el mismo puerto Serial, terminados en `\n`.

| Comando | Formato | Descripción |
|---|---|---|
| Servo target | `S:<float>\n` | Mueve el rack a esa posición normalizada [0.0=lleno – 1.0=gastado] |
| Reset electrodo | `R\n` | Vuelve el rack a home (nuevo electrodo insertado) |

Ejemplos:
```text
S:0.350\n   → rack al 35% (electrodo a mitad de vida)
S:0.000\n   → rack a home (electrodo nuevo)
R\n         → orden explícita de reset (misma acción que S:0.000 + home físico)
```

Unity envía `S:` a ~10 Hz mientras el simulador está activo.
Si no llega ningún comando durante **2 segundos**, el Arduino pasa a modo autónomo
(calcula el consumo por tiempo de arco) hasta que Unity vuelva a conectarse.

---

## Frecuencia

| Dirección | Señal | Frecuencia |
|---|---|---|
| Arduino → Unity | CSV telemetría | 30 Hz |
| Arduino → Unity | Lectura láser | 25 Hz |
| Unity → Arduino | Comando servo `S:` | 10 Hz |
