# Arduino Uno SMAW

Esta carpeta deja separado el firmware base del `Arduino Uno` para el MVP del simulador SMAW.

## Objetivo

El Arduino se encarga de:

- leer el `MPU6050`
- leer el sensor laser de distancia `VL53L0X`
- leer los `3 botones fisicos` de seleccion de electrodo
- leer un boton de `trigger / sesion activa`
- mover el servo que simula el consumo del electrodo
- enviar telemetria por `Serial USB`

## Comunicacion

Por ahora la comunicacion queda definida por `Serial` a `115200` baudios.

No se usa Bluetooth.

Cada linea sale como CSV y termina en `\\n`:

```text
timestampMs,laserMm,consumedMm,servo01,pitchDeg,rollDeg,yawDeg,triggerPressed,trackingConfidence,electrodeBtn
```

Ejemplo:

```text
1715001234567,3.42,18.50,0.074,12.60,-2.10,34.50,1,1.00,2
```

## Pinout propuesto

### Bus I2C

En `Arduino Uno`, tanto el `MPU6050` como el `VL53L0X` van al mismo bus:

- `A4` -> `SDA`
- `A5` -> `SCL`
- `5V` -> `VCC` del `MPU6050`
- `5V` o `3.3V` -> `VCC` del `VL53L0X` segun el modulo que tengan
- `GND` -> `GND`

Direcciones I2C tipicas:

- `MPU6050` -> `0x68`
- `VL53L0X` -> `0x29`

### Entradas digitales

- `D2` -> Boton electrodo 1 (`3/32`)
- `D3` -> Boton electrodo 2 (`1/8`)
- `D4` -> Boton electrodo 3 (`5/32`)
- `D5` -> Boton trigger / soldando

Todos esos botones estan pensados con `INPUT_PULLUP`, asi que van:

- un lado del boton al pin digital
- el otro lado a `GND`

### Servo

- `D9` -> senal servo
- `5V externa` -> alimentacion servo
- `GND externa` -> tierra servo
- `GND externa` unida a `GND` del Arduino

Importante:

- No alimenten el servo desde el `5V` del Arduino si el mecanismo tiene carga real.
- Usen una fuente externa de `5V` con tierra comun.

## Sensores soportados

El sketch esta pensado para:

- `MPU6050`
- `VL53L0X`

Si el sensor laser final no es `VL53L0X`, lo mas probable es que solo haya que cambiar el bloque de lectura de distancia en el `.ino`.

## Sobre el yaw

El `MPU6050` no tiene magnetometro.

Eso significa:

- `pitch` y `roll` salen razonablemente estables
- `yaw` sale integrado desde el giroscopio y deriva con el tiempo

Para este MVP sirve, pero hay que recalibrar al iniciar cada practica.

## Sobre el servo de 360

El sketch soporta dos modos:

1. `SERVO_MODE_POSITIONAL`
2. `SERVO_MODE_CONTINUOUS_ESTIMATED`

Como ustedes mencionaron un servo de `360`, el firmware viene por defecto en:

```cpp
SERVO_MODE_CONTINUOUS_ESTIMATED
```

Ese modo no conoce posicion real; aproxima la posicion usando tiempo.

Para demo MVP sirve, pero si luego quieren precision real del consumo, lo ideal es:

- cambiar a un servo posicional real, o
- agregar feedback de posicion

## Librerias requeridas

Instalar desde el Library Manager del Arduino IDE:

- `MPU6050_light`
- `Adafruit VL53L0X`

La libreria `Servo` y `Wire` ya vienen con el entorno Arduino.

## Archivos

- [ArduinoUno_SMAW.ino](</Users/usuario/Desktop/DigitalSmaw/Hardware/ArduinoUno_SMAW/ArduinoUno_SMAW.ino>)
- [SERIAL_PROTOCOL.md](</Users/usuario/Desktop/DigitalSmaw/Hardware/ArduinoUno_SMAW/SERIAL_PROTOCOL.md>)

## Nota de integracion con Unity

Este firmware queda orientado a una app que corre en `PC`.

La idea de trabajo pasa a ser:

- `Arduino Uno` por `USB`
- `Unity` en `PC`
- lectura directa del puerto `COM` / `tty`

El protocolo que emite este firmware ya es compatible con el formato esperado por el proyecto.
