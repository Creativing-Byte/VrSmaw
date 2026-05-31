#include <Wire.h>
#include <Servo.h>
#include <MPU6050_light.h>
#include <Adafruit_VL53L0X.h>

enum ServoControlMode : uint8_t
{
  SERVO_MODE_POSITIONAL = 0,
  SERVO_MODE_CONTINUOUS_ESTIMATED = 1
};

enum RackMotionState : uint8_t
{
  RACK_MOTION_IDLE = 0,
  RACK_MOTION_RETURNING_HOME = 1
};

// ---------------------------------------------------------------------------
// Hardware config
// ---------------------------------------------------------------------------

static const uint8_t PIN_BUTTON_ELECTRODE_1 = 2;
static const uint8_t PIN_BUTTON_ELECTRODE_2 = 3;
static const uint8_t PIN_BUTTON_ELECTRODE_3 = 4;
static const uint8_t PIN_TRIGGER            = 5;
static const uint8_t PIN_SERVO              = 9;

static const ServoControlMode SERVO_MODE = SERVO_MODE_CONTINUOUS_ESTIMATED;

// Positional servo config
static const int SERVO_POSITIONAL_MIN_DEG = 10;
static const int SERVO_POSITIONAL_MAX_DEG = 170;

// Continuous servo config
static const int SERVO_CONTINUOUS_NEUTRAL_US = 1500;
static const int SERVO_CONTINUOUS_FORWARD_US = 1680;
static const int SERVO_CONTINUOUS_REVERSE_US = 1320;
static const float SERVO_CONTINUOUS_TRACK_SECONDS = 8.0f;
static const float SERVO_CONTINUOUS_DEADBAND = 0.01f;
static const unsigned long SERVO_POSITIONAL_HOME_SETTLE_MS = 600UL;

// Electrode travel on rack
static const float MAX_ELECTRODE_TRAVEL_MM = 250.0f;

// Sensor and telemetry timing
static const unsigned long TELEMETRY_INTERVAL_MS = 33UL;
static const unsigned long LASER_INTERVAL_MS = 40UL;
static const float LASER_VALID_MIN_MM = 1.0f;
static const float LASER_VALID_MAX_MM = 120.0f;

// ---------------------------------------------------------------------------
// Domain config
// ---------------------------------------------------------------------------

struct ElectrodeProfile
{
  const char* code;
  float diameterMm;
  float baseConsumptionSeconds;
  float arcMinMm;
  float arcMaxMm;
};

static const ElectrodeProfile ELECTRODES[] =
{
  { "E6013 3/32", 2.4f, 47.0f, 2.0f, 3.0f },
  { "E6013 1/8",  3.2f, 56.0f, 3.0f, 4.0f },
  { "E6013 5/32", 4.0f, 89.0f, 4.0f, 5.0f }
};

static const uint8_t ELECTRODE_COUNT = sizeof(ELECTRODES) / sizeof(ELECTRODES[0]);

// ---------------------------------------------------------------------------
// Devices
// ---------------------------------------------------------------------------

MPU6050 mpu(Wire);
Adafruit_VL53L0X vl53 = Adafruit_VL53L0X();
Servo rackServo;

// ---------------------------------------------------------------------------
// Runtime state
// ---------------------------------------------------------------------------

bool imuReady = false;
bool laserReady = false;
bool buttonPrevStates[3] = { true, true, true };

uint8_t selectedElectrodeIndex = 0;
uint8_t requestedElectrodeIndex = 0;
int pendingElectrodePulse = 0;
RackMotionState rackMotionState = RACK_MOTION_IDLE;

float pitchDeg = 0.0f;
float rollDeg = 0.0f;
float yawDeg = 0.0f;
float laserDistanceMm = 0.0f;
float consumedMm = 0.0f;
float servoNormalized = 0.0f;
float estimatedServoNormalized = 0.0f;
float trackingConfidence = 0.0f;

unsigned long lastTelemetryMs = 0UL;
unsigned long lastLaserReadMs = 0UL;
unsigned long lastLoopMicros = 0UL;
unsigned long rackMotionStartedMs = 0UL;

// ---------------------------------------------------------------------------
// Forward declarations
// ---------------------------------------------------------------------------

float computeDeltaSeconds(unsigned long nowMicros);
void handleElectrodeButtons();
void requestElectrodeChange(uint8_t index);
void completeElectrodeChange();
void updateImu(float dt);
void updateLaserIfNeeded();
void updateConsumable(float dt);
void updateServo(float dt);
void updateTrackingConfidence();
void sendTelemetryIfNeeded();
bool isTriggerPressed();
bool isArcActive();

// ---------------------------------------------------------------------------
// Setup
// ---------------------------------------------------------------------------

void setup()
{
  Serial.begin(115200);

  pinMode(PIN_BUTTON_ELECTRODE_1, INPUT_PULLUP);
  pinMode(PIN_BUTTON_ELECTRODE_2, INPUT_PULLUP);
  pinMode(PIN_BUTTON_ELECTRODE_3, INPUT_PULLUP);
  pinMode(PIN_TRIGGER, INPUT_PULLUP);

  Wire.begin();
  Wire.setClock(400000UL);

  setupImu();
  setupLaser();
  setupServo();

  lastLoopMicros = micros();
}

void loop()
{
  const unsigned long nowMicros = micros();
  const float dt = computeDeltaSeconds(nowMicros);

  handleElectrodeButtons();
  updateImu(dt);
  updateLaserIfNeeded();
  updateConsumable(dt);
  updateServo(dt);
  updateTrackingConfidence();
  sendTelemetryIfNeeded();
}

// ---------------------------------------------------------------------------
// Device setup
// ---------------------------------------------------------------------------

void setupImu()
{
  byte imuStatus = mpu.begin();
  if (imuStatus == 0)
  {
    delay(100);
    mpu.calcOffsets(true, true);
    imuReady = true;
  }
  else
  {
    imuReady = false;
  }
}

void setupLaser()
{
  laserReady = vl53.begin();
}

void setupServo()
{
  rackServo.attach(PIN_SERVO);

  if (SERVO_MODE == SERVO_MODE_POSITIONAL)
  {
    rackServo.write(SERVO_POSITIONAL_MIN_DEG);
  }
  else
  {
    rackServo.writeMicroseconds(SERVO_CONTINUOUS_NEUTRAL_US);
  }
}

// ---------------------------------------------------------------------------
// Runtime update
// ---------------------------------------------------------------------------

float computeDeltaSeconds(unsigned long nowMicros)
{
  if (lastLoopMicros == 0UL)
  {
    lastLoopMicros = nowMicros;
    return 0.0f;
  }

  unsigned long deltaMicros = nowMicros - lastLoopMicros;
  lastLoopMicros = nowMicros;
  return (float)deltaMicros / 1000000.0f;
}

void handleElectrodeButtons()
{
  const bool buttonStates[3] =
  {
    digitalRead(PIN_BUTTON_ELECTRODE_1) == LOW,
    digitalRead(PIN_BUTTON_ELECTRODE_2) == LOW,
    digitalRead(PIN_BUTTON_ELECTRODE_3) == LOW
  };

  for (uint8_t i = 0; i < 3; ++i)
  {
    if (buttonStates[i] && !buttonPrevStates[i])
    {
      requestElectrodeChange(i);
      pendingElectrodePulse = (int)i + 1;
    }

    buttonPrevStates[i] = buttonStates[i];
  }
}

void requestElectrodeChange(uint8_t index)
{
  if (index >= ELECTRODE_COUNT)
  {
    return;
  }

  requestedElectrodeIndex = index;
  consumedMm = 0.0f;
  servoNormalized = 0.0f;
  rackMotionState = RACK_MOTION_RETURNING_HOME;
  rackMotionStartedMs = millis();

  if (SERVO_MODE == SERVO_MODE_POSITIONAL)
  {
    return;
  }

  if (estimatedServoNormalized <= SERVO_CONTINUOUS_DEADBAND)
  {
    completeElectrodeChange();
  }
}

void completeElectrodeChange()
{
  selectedElectrodeIndex = requestedElectrodeIndex;
  consumedMm = 0.0f;
  servoNormalized = 0.0f;
  estimatedServoNormalized = 0.0f;
  rackMotionState = RACK_MOTION_IDLE;
  rackMotionStartedMs = 0UL;
}

void updateImu(float dt)
{
  if (!imuReady)
  {
    pitchDeg = 0.0f;
    rollDeg = 0.0f;
    yawDeg = 0.0f;
    return;
  }

  mpu.update();

  pitchDeg = mpu.getAngleX();
  rollDeg = mpu.getAngleY();
  yawDeg += mpu.getGyroZ() * dt;

  if (yawDeg > 180.0f) yawDeg -= 360.0f;
  if (yawDeg < -180.0f) yawDeg += 360.0f;
}

void updateLaserIfNeeded()
{
  unsigned long nowMs = millis();
  if (nowMs - lastLaserReadMs < LASER_INTERVAL_MS)
  {
    return;
  }

  lastLaserReadMs = nowMs;

  if (!laserReady)
  {
    laserDistanceMm = 0.0f;
    return;
  }

  VL53L0X_RangingMeasurementData_t measure;
  vl53.rangingTest(&measure, false);

  if (measure.RangeStatus == 4)
  {
    laserDistanceMm = 0.0f;
    return;
  }

  float distance = (float)measure.RangeMilliMeter;
  if (distance < LASER_VALID_MIN_MM || distance > LASER_VALID_MAX_MM)
  {
    laserDistanceMm = 0.0f;
    return;
  }

  laserDistanceMm = distance;
}

void updateConsumable(float dt)
{
  if (dt <= 0.0f)
  {
    return;
  }

  if (rackMotionState != RACK_MOTION_IDLE)
  {
    return;
  }

  if (!isArcActive())
  {
    return;
  }

  const ElectrodeProfile& electrode = ELECTRODES[selectedElectrodeIndex];
  float mmPerSecond = MAX_ELECTRODE_TRAVEL_MM / electrode.baseConsumptionSeconds;
  consumedMm += mmPerSecond * dt;
  if (consumedMm > MAX_ELECTRODE_TRAVEL_MM)
  {
    consumedMm = MAX_ELECTRODE_TRAVEL_MM;
  }

  servoNormalized = consumedMm / MAX_ELECTRODE_TRAVEL_MM;
  if (servoNormalized < 0.0f) servoNormalized = 0.0f;
  if (servoNormalized > 1.0f) servoNormalized = 1.0f;
}

void updateServo(float dt)
{
  float targetNormalized = rackMotionState == RACK_MOTION_RETURNING_HOME ? 0.0f : servoNormalized;

  if (SERVO_MODE == SERVO_MODE_POSITIONAL)
  {
    int targetDeg = SERVO_POSITIONAL_MIN_DEG +
      (int)((SERVO_POSITIONAL_MAX_DEG - SERVO_POSITIONAL_MIN_DEG) * targetNormalized);
    rackServo.write(targetDeg);

    if (rackMotionState == RACK_MOTION_RETURNING_HOME)
    {
      unsigned long elapsedMs = millis() - rackMotionStartedMs;
      if (elapsedMs >= SERVO_POSITIONAL_HOME_SETTLE_MS)
      {
        completeElectrodeChange();
      }
    }

    return;
  }

  // Continuous rotation, estimated by time.
  float diff = targetNormalized - estimatedServoNormalized;
  if (diff > SERVO_CONTINUOUS_DEADBAND)
  {
    rackServo.writeMicroseconds(SERVO_CONTINUOUS_FORWARD_US);
    estimatedServoNormalized += dt / SERVO_CONTINUOUS_TRACK_SECONDS;
  }
  else if (diff < -SERVO_CONTINUOUS_DEADBAND)
  {
    rackServo.writeMicroseconds(SERVO_CONTINUOUS_REVERSE_US);
    estimatedServoNormalized -= dt / SERVO_CONTINUOUS_TRACK_SECONDS;
  }
  else
  {
    rackServo.writeMicroseconds(SERVO_CONTINUOUS_NEUTRAL_US);
  }

  if (estimatedServoNormalized < 0.0f) estimatedServoNormalized = 0.0f;
  if (estimatedServoNormalized > 1.0f) estimatedServoNormalized = 1.0f;

  if (rackMotionState == RACK_MOTION_RETURNING_HOME &&
      estimatedServoNormalized <= SERVO_CONTINUOUS_DEADBAND)
  {
    rackServo.writeMicroseconds(SERVO_CONTINUOUS_NEUTRAL_US);
    completeElectrodeChange();
  }
}

void updateTrackingConfidence()
{
  trackingConfidence = 0.0f;

  if (imuReady)
  {
    trackingConfidence += 0.5f;
  }

  if (laserReady && laserDistanceMm > 0.0f)
  {
    trackingConfidence += 0.5f;
  }
}

void sendTelemetryIfNeeded()
{
  unsigned long nowMs = millis();
  if (nowMs - lastTelemetryMs < TELEMETRY_INTERVAL_MS)
  {
    return;
  }

  lastTelemetryMs = nowMs;

  Serial.print(nowMs);
  Serial.print(',');
  Serial.print(laserDistanceMm, 2);
  Serial.print(',');
  Serial.print(consumedMm, 2);
  Serial.print(',');
  Serial.print(servoNormalized, 3);
  Serial.print(',');
  Serial.print(pitchDeg, 2);
  Serial.print(',');
  Serial.print(rollDeg, 2);
  Serial.print(',');
  Serial.print(yawDeg, 2);
  Serial.print(',');
  Serial.print(isTriggerPressed() ? 1 : 0);
  Serial.print(',');
  Serial.print(trackingConfidence, 2);
  Serial.print(',');
  Serial.println(pendingElectrodePulse);

  pendingElectrodePulse = 0;
}

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

bool isTriggerPressed()
{
  return digitalRead(PIN_TRIGGER) == LOW;
}

bool isArcActive()
{
  if (rackMotionState != RACK_MOTION_IDLE)
  {
    return false;
  }

  if (!isTriggerPressed())
  {
    return false;
  }

  const ElectrodeProfile& electrode = ELECTRODES[selectedElectrodeIndex];
  return laserDistanceMm >= electrode.arcMinMm && laserDistanceMm <= electrode.arcMaxMm;
}
