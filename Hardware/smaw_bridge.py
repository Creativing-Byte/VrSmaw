#!/usr/bin/env python3
"""
SMAW Bridge — conecta el Arduino Uno con el Quest 3 vía UDP.

Zero config: no hay que saber la IP del Quest.
El bridge hace broadcast de la telemetría al segmento de red local;
el Quest la recibe, descubre automáticamente la IP del bridge y
envía los comandos de vuelta.

Flujo:
  Arduino (Serial) ---> bridge ---> UDP broadcast:9100 ---> Quest 3
  Quest 3 ---> UDP:9101 ---> bridge ---> Serial ---> Arduino

Uso:
  python smaw_bridge.py

Opciones:
  --port   Puerto serial del Arduino (default: auto-detect)
  --baud   Baudios               (default: 115200)
  --tin    Puerto telemetría saliente  (default: 9100)
  --cin    Puerto comandos entrantes   (default: 9101)

Dependencias:
  pip install pyserial
"""

import argparse
import serial
import serial.tools.list_ports
import socket
import threading
import time
import sys

TELEMETRY_UDP_PORT = 9100   # Quest escucha aquí
COMMAND_UDP_PORT   = 9101   # Bridge escucha aquí, recibe comandos del Quest
BROADCAST_ADDR     = "255.255.255.255"

ARDUINO_HINTS = ["Arduino", "usbmodem", "wchusbserial", "ttyACM", "ttyUSB", "CH340", "CP210"]

# ---------------------------------------------------------------------------
# Serial auto-detect
# ---------------------------------------------------------------------------

def find_arduino_port():
    ports = serial.tools.list_ports.comports()
    for p in ports:
        desc = (p.description or "") + (p.manufacturer or "") + (p.device or "")
        for hint in ARDUINO_HINTS:
            if hint.lower() in desc.lower():
                return p.device
    if ports:
        return ports[0].device
    return None

# ---------------------------------------------------------------------------
# Bridge threads
# ---------------------------------------------------------------------------

def serial_to_udp(ser, udp_sock, running):
    """
    Lee líneas del Arduino y las emite por broadcast UDP.
    Cualquier dispositivo en la red (Quest 3) las recibe sin config previa.
    """
    target = (BROADCAST_ADDR, TELEMETRY_UDP_PORT)
    print(f"[bridge] Telemetría → broadcast {BROADCAST_ADDR}:{TELEMETRY_UDP_PORT}")
    while running[0]:
        try:
            line = ser.readline()
            if line:
                udp_sock.sendto(line, target)
        except serial.SerialException as e:
            print(f"[bridge] Serial error: {e}")
            running[0] = False
            break
        except OSError:
            break

def udp_to_serial(ser, cmd_sock, running):
    """
    Escucha comandos del Quest y los escribe al Arduino.
    Imprime la IP del Quest la primera vez para confirmar la conexión.
    """
    print(f"[bridge] Comandos ← UDP:{COMMAND_UDP_PORT}")
    cmd_sock.settimeout(1.0)
    known_quest_ip = None
    while running[0]:
        try:
            data, addr = cmd_sock.recvfrom(64)
            if data:
                if addr[0] != known_quest_ip:
                    known_quest_ip = addr[0]
                    print(f"[bridge] Quest conectado desde {addr[0]}")
                ser.write(data)
        except socket.timeout:
            continue
        except OSError:
            break

# ---------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------

def main():
    parser = argparse.ArgumentParser(description="SMAW Arduino ↔ Quest 3 UDP bridge (zero-config)")
    parser.add_argument("--port", default=None, help="Puerto serial (ej: COM3 o /dev/ttyACM0)")
    parser.add_argument("--baud", default=115200, type=int)
    args = parser.parse_args()

    # --- Resolve serial port ---
    port = args.port or find_arduino_port()
    if port is None:
        print("[bridge] ERROR: no se encontró el Arduino. Conecta el USB o usa --port.")
        sys.exit(1)
    print(f"[bridge] Arduino en: {port}")

    # --- Open serial ---
    try:
        ser = serial.Serial(port, args.baud, timeout=1.0)
        time.sleep(2.0)   # espera reset del Arduino al abrir el puerto
        ser.reset_input_buffer()
        print(f"[bridge] Serial abierto a {args.baud} baudios")
    except serial.SerialException as e:
        print(f"[bridge] ERROR abriendo serial: {e}")
        sys.exit(1)

    # --- UDP sockets ---
    udp_send = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    udp_send.setsockopt(socket.SOL_SOCKET, socket.SO_BROADCAST, 1)  # habilita broadcast

    udp_recv = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    udp_recv.bind(("0.0.0.0", COMMAND_UDP_PORT))

    running = [True]

    t1 = threading.Thread(target=serial_to_udp, args=(ser, udp_send, running), daemon=True)
    t2 = threading.Thread(target=udp_to_serial, args=(ser, udp_recv, running), daemon=True)

    t1.start()
    t2.start()

    print("[bridge] Corriendo — esperando Quest 3 en la red. Ctrl+C para detener.")
    try:
        while running[0]:
            time.sleep(0.5)
    except KeyboardInterrupt:
        print("\n[bridge] Deteniendo...")

    running[0] = False
    try: ser.close()
    except: pass
    try: udp_send.close()
    except: pass
    try: udp_recv.close()
    except: pass
    t1.join(timeout=2)
    t2.join(timeout=2)
    print("[bridge] Cerrado.")

if __name__ == "__main__":
    main()
