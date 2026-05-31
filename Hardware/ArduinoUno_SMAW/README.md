# Arduino Uno SMAW — Guía de conexión

Esta guía explica cómo conectar el Arduino al simulador en el Quest 3, paso a paso.
No necesitas saber programación. Solo sigue los pasos en orden.

---

## Qué hace el Arduino

- Lee el sensor láser de distancia (VL53L0X) — detecta qué tan lejos está el workpiece
- Lee el IMU (MPU6050) — mide la inclinación del electrodo
- Lee los 3 botones físicos de selección de electrodo
- Lee el botón de trigger (soldando / no soldando)
- Mueve el servo que empuja el rack del electrodo físico para que coincida con lo que ves en las gafas

---

## Lo que necesitas

- Arduino Uno
- Cable USB tipo B (el cuadrado, el que usa el Arduino)
- PC con Windows, Mac o Linux
- Quest 3 conectado a la misma red WiFi que el PC
- Arduino IDE instalado
- Python instalado (ver instrucciones abajo si no lo tienes)

---

## Paso 1 — Instalar Python (solo si no lo tienes)

Python es el lenguaje en el que está escrito el programa puente entre el Arduino y el Quest.

**¿Cómo saber si ya lo tienes?**
Abre una terminal (en Windows: busca "cmd" o "PowerShell") y escribe:
```
python --version
```
Si aparece algo como `Python 3.x.x` ya lo tienes y puedes saltar al Paso 2.

**Si no lo tienes:**

- Entra a [https://www.python.org/downloads/](https://www.python.org/downloads/)
- Haz clic en el botón amarillo grande **"Download Python 3.x.x"**
- Ejecuta el instalador
- **IMPORTANTE:** en la primera pantalla del instalador, marca la casilla que dice **"Add Python to PATH"** antes de hacer clic en Install

Cuando termine, cierra y vuelve a abrir la terminal y prueba de nuevo `python --version`.

---

## Paso 2 — Instalar la librería de comunicación Serial

Esto solo se hace una vez. En la terminal escribe:

```
pip install pyserial
```

Espera a que termine. Verás algo como `Successfully installed pyserial-x.x`.

---

## Paso 3 — Instalar las librerías en el Arduino IDE

Abre el Arduino IDE. Ve a **Herramientas → Administrar bibliotecas** y busca e instala estas dos:

- `MPU6050_light` (autor: rfetick)
- `Adafruit VL53L0X`

Las librerías `Servo` y `Wire` ya vienen incluidas con el Arduino IDE, no hay que instalarlas.

---

## Paso 4 — Subir el firmware al Arduino

1. Conecta el Arduino al PC con el cable USB
2. Abre el archivo `ArduinoUno_SMAW.ino` en el Arduino IDE
3. Ve a **Herramientas → Placa** y selecciona **Arduino Uno**
4. Ve a **Herramientas → Puerto** y selecciona el puerto que aparece (algo como `COM3`, `COM4` en Windows, o `/dev/ttyACM0` en Linux/Mac)
5. Haz clic en el botón **Subir** (la flecha hacia la derecha)
6. Espera a que diga "Subida completada"

El firmware solo hay que subirlo una vez. Las siguientes veces que uses el Arduino no tienes que repetir este paso.

---

## Paso 5 — Correr el bridge (cada vez que uses el simulador)

El bridge es el programa que conecta el Arduino con el Quest 3. Tiene que estar corriendo en el PC mientras usas las gafas.

1. Asegúrate de que el Arduino esté conectado al PC por USB
2. Asegúrate de que el PC y el Quest 3 estén en la **misma red WiFi**
3. Abre una terminal y navega a la carpeta del proyecto:
   ```
   cd ruta/al/proyecto/DigitalSmaw/Hardware
   ```
4. Corre el bridge:
   ```
   python smaw_bridge.py
   ```

Si todo va bien verás esto en la terminal:
```
[bridge] Arduino en: COM4
[bridge] Serial abierto a 115200 baudios
[bridge] Telemetría → broadcast 255.255.255.255:9100
[bridge] Comandos ← UDP:9101
[bridge] Corriendo — esperando Quest 3 en la red. Ctrl+C para detener.
```

Cuando el Quest 3 se conecte, aparecerá:
```
[bridge] Quest conectado desde 192.168.1.50
```

Para detener el bridge cuando termines, presiona `Ctrl + C` en la terminal.

---

## Paso 6 — Lanzar la app en el Quest 3

Con el bridge corriendo en el PC, abre la app del simulador en el Quest normalmente.
La conexión con el Arduino es automática — no hay que configurar ninguna IP.

---

## Solución de problemas

**"No se encontró el Arduino"**
El bridge no detectó el puerto serial. Prueba indicando el puerto manualmente:
```
python smaw_bridge.py --port COM4
```
(En Windows busca el número de puerto en el Administrador de dispositivos → Puertos COM y LPT)

**El Quest no recibe datos del Arduino**
- Confirma que el PC y el Quest 3 están en la misma red WiFi (no es suficiente que ambos tengan WiFi, deben estar conectados al mismo router)
- Algunos routers de empresas o universitarios bloquean el broadcast UDP. En ese caso usa un router doméstico o un hotspot del móvil

**El servo no se mueve / el rack físico no coincide con las gafas**
- Comprueba que el bridge esté corriendo (la terminal no muestra errores)
- Comprueba que la app del Quest haya recibido telemetría (debe aparecer el mensaje "Quest conectado desde...")

---

## Pinout del hardware

### Bus I2C (MPU6050 y VL53L0X comparten el mismo bus)

| Arduino | Sensor |
|---|---|
| A4 | SDA |
| A5 | SCL |
| 5V | VCC del MPU6050 |
| 5V o 3.3V | VCC del VL53L0X (según el módulo) |
| GND | GND |

Direcciones I2C: MPU6050 → `0x68` · VL53L0X → `0x29`

### Entradas digitales (todos con INPUT_PULLUP → un lado al pin, otro a GND)

| Pin | Función |
|---|---|
| D2 | Botón electrodo 1 (E6013 3/32") |
| D3 | Botón electrodo 2 (E6013 1/8") |
| D4 | Botón electrodo 3 (E6013 5/32") |
| D5 | Botón trigger (soldando) |

### Servo del rack

| | |
|---|---|
| D9 | Señal del servo |
| 5V externa | Alimentación del servo |
| GND externa | Tierra del servo (unida al GND del Arduino) |

> No alimentes el servo desde el 5V del Arduino si el mecanismo tiene carga real. Usa una fuente externa de 5V con tierra común.

---

## Notas técnicas

- **Yaw**: el MPU6050 no tiene magnetómetro. El yaw se integra desde el giroscopio y acumula deriva con el tiempo. Es suficiente para el MVP pero conviene recalibrar al iniciar cada práctica.
- **Servo de 360°**: el firmware usa `SERVO_MODE_CONTINUOUS_ESTIMATED`. No conoce la posición real del rack — la estima por tiempo. Si en el futuro se quiere más precisión, lo ideal es agregar un sensor de home (endstop) o cambiar a un servo posicional.
- **Protocolo**: ver [SERIAL_PROTOCOL.md](SERIAL_PROTOCOL.md) para el detalle técnico de los mensajes.
