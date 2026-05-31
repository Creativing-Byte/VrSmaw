#!/usr/bin/env python3
"""
SMAW Bridge — conecta el Arduino Uno con el Quest 3 vía UDP.

Flujo:
  Arduino (Serial) ---> bridge ---> UDP:9100 ---> Quest 3  (telemetría)
  Quest 3 ---> UDP:9101 ---> bridge ---> Serial ---> Arduino  (comandos servo)

Uso:
  python smaw_bridge.py --quest <IP_QUEST>

Ejemplo:
  python smaw_bridge.py --quest 192.168.1.50

Opciones:
  --quest   IP del Quest 3 en la red WiFi  (obligatorio)
  --port    Puerto serial del Arduino      (default: auto-detect)
  --baud    Baudios                        (default: 115200)
  --in      Puerto UDP recepción telemetría (default: 9100, igual que Unity espera)
  --out     Puerto UDP recepción comandos  (default: 9101)

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

# ---------------------------------------------------------------------------
# Config
# ---------------------------------------------------------------------------

TELEMETRY_UDP_PORT = 9100   # Quest escucha aquí (ArduinoBridgeReceiver)
COMMAND_UDP_PORT   = 9101   # Bridge escucha aquí, recibe comandos de Quest

# ---------------------------------------------------------------------------
# Serial auto-detect
# ---------------------------------------------------------------------------

ARDUINO_HINTS = ["Arduino", "usbmodem", "wchusbserial", "ttyACM", "ttyUSB", "CH340", "CP210"]

def find_arduino_port():
    ports = serial.tools.list_ports.comports()
    for p in ports:
        desc = (p.description or "") + (p.manufacturer or "") + (p.device or "")
        for hint in ARDUINO_HINTS:
            if hint.lower() in desc.lower():
                return p.device
    # Fallback: first available port
    if ports:
        return ports[0].device
    return None

# ---------------------------------------------------------------------------
# Bridge threads
# ---------------------------------------------------------------------------

def serial_to_udp(ser, quest_ip, udp_sock, running):
    """Lee líneas del Arduino y las reenvía al Quest por UDP."""
    target = (quest_ip, TELEMETRY_UDP_PORT)
    print(f"[bridge] Telemetría Serial → UDP {quest_ip}:{TELEMETRY_UDP_PORT}")
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
    """Escucha comandos de Unity/Quest y los escribe al Arduino por Serial."""
    print(f"[bridge] Comandos UDP:{COMMAND_UDP_PORT} → Serial")
    cmd_sock.settimeout(1.0)
    while running[0]:
        try:
            data, addr = cmd_sock.recvfrom(64)
            if data:
                ser.write(data)   # ya viene con \n incluido
        except socket.timeout:
            continue
        except OSError:
            break

# ---------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------

def main():
    parser = argparse.ArgumentParser(description="SMAW Arduino ↔ Quest 3 UDP bridge")
    parser.add_argument("--quest", required=True, help="IP del Quest 3 en la red WiFi")
    parser.add_argument("--port",  default=None,  help="Puerto serial del Arduino (ej: COM3 o /dev/ttyACM0)")
    parser.add_argument("--baud",  default=115200, type=int)
    args = parser.parse_args()

    # --- Resolve serial port ---
    port = args.port
    if port is None:
        port = find_arduino_port()
        if port is None:
            print("[bridge] ERROR: no se encontró el Arduino. Conecta el USB o usa --port.")
            sys.exit(1)
        print(f"[bridge] Arduino detectado en: {port}")
    else:
        print(f"[bridge] Usando puerto: {port}")

    # --- Open serial ---
    try:
        ser = serial.Serial(port, args.baud, timeout=1.0)
        time.sleep(2.0)   # espera reset del Arduino al abrir el puerto
        ser.reset_input_buffer()
        print(f"[bridge] Serial abierto a {args.baud} baudios")
    except serial.SerialException as e:
        print(f"[bridge] ERROR abriendo serial: {e}")
        sys.exit(1)

    # --- Open UDP sockets ---
    udp_send = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    udp_recv = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    udp_recv.bind(("0.0.0.0", COMMAND_UDP_PORT))
    print(f"[bridge] Quest IP: {args.quest}")

    running = [True]

    t1 = threading.Thread(target=serial_to_udp,
                          args=(ser, args.quest, udp_send, running),
                          daemon=True)
    t2 = threading.Thread(target=udp_to_serial,
                          args=(ser, udp_recv, running),
                          daemon=True)

    t1.start()
    t2.start()

    print("[bridge] Corriendo. Ctrl+C para detener.")
    try:
        while running[0]:
            time.sleep(0.5)
    except KeyboardInterrupt:
        print("\n[bridge] Deteniendo...")

    running[0] = False
    ser.close()
    udp_send.close()
    udp_recv.close()
    t1.join(timeout=2)
    t2.join(timeout=2)
    print("[bridge] Cerrado.")

if __name__ == "__main__":
    main()
