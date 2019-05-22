﻿// COPYRIGHT 2013 by the Open Rails project.
// 
// This file is part of Open Rails.
// 
// Open Rails is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
// 
// Open Rails is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
// 
// You should have received a copy of the GNU General Public License
// along with Open Rails.  If not, see <http://www.gnu.org/licenses/>.

/* TRAINS
 * 
 * Contains code to represent a train as a list of TrainCars and to handle the physics of moving
 * the train through the Track Database.
 * 
 * A train has:
 *  - a list of TrainCars 
 *  - a front and back position in the TDB ( represented by TDBTravellers )
 *  - speed
 *  - MU signals that are relayed from player locomtive to other locomotives and cars such as:
 *      - direction
 *      - throttle percent
 *      - brake percent  ( TODO, this should be changed to brake pipe pressure )
 *      
 *  Individual TrainCars provide information on friction and motive force they are generating.
 *  This is consolidated by the train class into overall movement for the train.
 */

// Compiler flags for debug print-out facilities
// #define DEBUG_TEST
// #define DEBUG_REPORTS
// #define DEBUG_DEADLOCK
// #define DEBUG_TRACEINFO
// #define DEBUG_SIGNALPASS

// Debug Calculation of Carriage Heat Loss
// #define DEBUG_CARSTEAMHEAT

// Debug Calculation of Aux Tender operation
// #define DEBUG_AUXTENDER

// Debug for calculation of speed forces
// #define DEBUG_SPEED_FORCES

using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Orts.Formats.Msts;
using Orts.MultiPlayer;
using Orts.Simulation.AIs;
using Orts.Simulation.RollingStocks;
using Orts.Simulation.RollingStocks.SubSystems.Brakes;
using Orts.Simulation.RollingStocks.SubSystems.Brakes.MSTS;
using Orts.Parsers.Msts;
using Orts.Simulation.Signalling;
using Orts.Simulation.Timetables;
using ORTS.Common;
using ORTS.Scripting.Api;
using ORTS.Settings;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Event = Orts.Common.Event;

namespace Orts.Simulation.Physics
{
    public class Train
    {
        public List<TrainCar> Cars = new List<TrainCar>();           // listed front to back
        public int Number;
        public string Name;
        public static int TotalNumber = 1; // start at 1 (0 is reserved for player train)
        public TrainCar FirstCar
        {
            get
            {
                return Cars[0];
            }
        }
        public TrainCar LastCar
        {
            get
            {
                return Cars[Cars.Count - 1];
            }
        }
        public Traveller RearTDBTraveller;               // positioned at the back of the last car in the train
        public Traveller FrontTDBTraveller;              // positioned at the front of the train by CalculatePositionOfCars
        public float Length;                             // length of train from FrontTDBTraveller to RearTDBTraveller
        public float MassKg;                             // weight of the train
        public float SpeedMpS;                           // meters per second +ve forward, -ve when backing
        float LastSpeedMpS;                              // variable to remember last speed used for projected speed
        public SmoothedData AccelerationMpSpS = new SmoothedData(); // smoothed acceleration data
        public float ProjectedSpeedMpS;                  // projected speed
        public float LastReportedSpeed;

        public Train UncoupledFrom;                      // train not to coupled back onto
        public float TotalCouplerSlackM;
        public float MaximumCouplerForceN;
        public int NPull;
        public int NPush;
        public int LeadLocomotiveIndex = -1;
        public bool IsFreight;                           // has at least one freight car
        public int PassengerCarsNumber = 0;              // Number of passenger cars
        public float SlipperySpotDistanceM;              // distance to extra slippery part of track
        public float SlipperySpotLengthM;

        public float WagonCoefficientFriction = 0.35f; // Initialise coefficient of Friction for wagons - 0.35 for dry rails, 0.1 - 0.25 for wet rails
        public float LocomotiveCoefficientFriction = 0.35f; // Initialise coefficient of Friction for locomotives - 0.5 for dry rails, 0.1 - 0.25 for wet rails

        // These signals pass through to all cars and locomotives on the train
        public Direction MUDirection = Direction.N;      // set by player locomotive to control MU'd locomotives
        public float MUThrottlePercent;                  // set by player locomotive to control MU'd locomotives
        public int MUGearboxGearIndex;                   // set by player locomotive to control MU'd locomotives
        public float MUReverserPercent = 100;            // steam engine direction/cutoff control for MU'd locomotives
        public float MUDynamicBrakePercent = -1;         // dynamic brake control for MU'd locomotives, <0 for off
        public float EqualReservoirPressurePSIorInHg = 90;   // Pressure in equalising reservoir - set by player locomotive - train brake pipe use this as a reference to set brake pressure levels

        // Class AirSinglePipe etc. use this property for pressure in PSI, 
        // but Class VacuumSinglePipe uses it for vacuum in InHg.
        public float BrakeLine2PressurePSI;              // extra line for dual line systems, main reservoir
        public float BrakeLine3PressurePSI;              // extra line just in case, engine brake pressure
        public float BrakeLine4 = -1;                    // extra line just in case, ep brake control line. -1: release/inactive, 0: hold, 0 < value <=1: apply
        public RetainerSetting RetainerSetting = RetainerSetting.Exhaust;
        public int RetainerPercent = 100;
        public float TotalTrainBrakePipeVolumeM3; // Total volume of train brake pipe
        public float TotalTrainBrakeCylinderVolumeM3; // Total volume of train brake cylinders
        public float TotalTrainBrakeSystemVolumeM3; // Total volume of train brake system
        public float TotalCurrentTrainBrakeSystemVolumeM3; // Total current volume of train brake system
        public bool EQEquippedVacLoco = false;          // Flag for locomotives fitted with vacuum brakes that have an Equalising reservoir fitted
        public float PreviousCarCount;                  // Keeps track of the last number of cars in the train consist (for vacuum brakes)
        public bool TrainBPIntact = true;           // Flag to indicate that the train BP is not intact, ie due to disconnection or an open valve cock.

        public int FirstCarUiD;                          // UiD of first car in the train
        public float HUDWagonBrakeCylinderPSI;         // Display value for wagon HUD
        public float HUDLocomotiveBrakeCylinderPSI;    // Display value for locomotive HUD
        public bool HUDBrakeSlide;                     // Display indication for brake wheel slip
        public bool WagonsAttached = false;    // Wagons are attached to train
        public float LeadPipePressurePSI;       // Keeps record of Lead locomootive brake pipe pressure

        public bool IsWheelSlipWarninq;
        public bool IsWheelSlip;
        public bool IsBrakeSkid;

        // Carriage Steam Heating
        public float TrainCurrentCarriageHeatTempC;     // Current train carriage heat
        public float TrainInsideTempC;                  // Desired inside temperature for carriage steam heating depending upon season
        public float TrainOutsideTempC;                 // External ambient temeprature for carriage steam heating.
        public float TrainSteamHeatLossWpT;             // Total Steam Heat loss of train
        public float TrainHeatVolumeM3;                 // Total Volume of train to steam heat
        public float TrainHeatPipeAreaM2;               // Total area of heating pipe for steam heating
        public float TrainCurrentSteamHeatPipeTempC;                 // Temperature of steam in steam heat system based upon pressure setting
//        public bool TrainFittedSteamHeat = false;               // Flag to determine train fitted with steam heating
        public bool CarSteamHeatOn = false;    // Is steam heating turned on
        public float TrainNetSteamHeatLossWpTime;        // Net Steam loss - Loss in Cars vs Steam Pipe Heat
        public float TrainCurrentTrainSteamHeatW;    // Current steam heat of air in train
        public float TrainSteamPipeHeatW;               // Heat radiated by steam pipe - total
        public float TrainTotalSteamHeatW;         // Total steam heat in train - based upon air volume
        float SpecificHeatCapcityAirKJpKgK = 1006.0f; // Specific Heat Capacity of Air
        float DensityAirKgpM3 = 1.247f;   // Density of air - use a av value
        bool IsSteamHeatExceeded = false;   // Flag to indicate when steam heat temp is exceeded
        bool IsSteamHeatLow = false;        // Flag to indicate when steam heat temp is low
        public float DisplayTrainNetSteamHeatLossWpTime;  // Display Net Steam loss - Loss in Cars vs Steam Pipe Heat
        public float TrainSteamPipeHeatConvW;               // Heat radiated by steam pipe - convection
        public float TrainSteamHeatPipeRadW;                // Heat radiated by steam pipe - radiation
        float PipeHeatTransCoeffWpM2K = 22.0f;    // heat transmission coefficient for a steel pipe.
        float BoltzmanConstPipeWpM2 = 0.0000000567f; // Boltzman's Constant
        bool IsTrainSteamHeatInitial = true; // Allow steam heat to be initialised.
        Interpolator OutsideWinterTempbyLatitudeC;
        Interpolator OutsideAutumnTempbyLatitudeC;
        Interpolator OutsideSpringTempbyLatitudeC;
        Interpolator OutsideSummerTempbyLatitudeC;

        // Values for Wind Direction and Speed - needed for wind resistance and lateral force
        public float PhysicsWindDirectionDeg;
        public float PhysicsWindSpeedMpS;
        public float PhysicsTrainLocoDirectionDeg;
        public float ResultantWindComponentDeg;
        public float WindResultantSpeedMpS;
        public bool TrainWindResistanceDependent
        {
            get
            {
               return Simulator.Settings.WindResistanceDependent;
            }
        }
        

        // Input values to allow the temperature for different values of latitude to be calculated
        static float[] WorldLatitudeDeg = new float[]
        {
           -50.0f, -40.0f, -30.0f, -20.0f, -10.0f, 0.0f, 10.0f, 20.0f, 30.0f, 40.0f, 50.0f, 60.0f
        };

        // Temperature in deg Celcius
        static float[] WorldTemperatureWinter = new float[]
        {
            0.9f, 8.7f, 12.4f, 17.2f, 20.9f, 25.9f, 22.8f, 18.2f, 11.1f, 1.1f, -10.2f, -18.7f
         };

        static float[] WorldTemperatureAutumn = new float[]
        {
            7.5f, 13.7f, 18.8f, 22.0f, 24.0f, 26.0f, 25.0f, 21.6f, 21.0f, 14.3f, 6.0f, 3.8f
         };

        static float[] WorldTemperatureSpring = new float[]
        {
            8.5f, 13.1f, 17.6f, 18.6f, 24.6f, 25.9f, 26.8f, 23.4f, 18.5f, 12.6f, 6.1f, 1.7f
         };

        static float[] WorldTemperatureSummer = new float[]
        {
            13.4f, 18.3f, 22.8f, 24.3f, 24.4f, 25.0f, 25.2f, 22.5f, 26.6f, 24.8f, 19.4f, 14.3f
         };

        public static Interpolator WorldWinterLatitudetoTemperatureC()
        {
            return new Interpolator(WorldLatitudeDeg, WorldTemperatureWinter);
        }

        public static Interpolator WorldAutumnLatitudetoTemperatureC()
        {
            return new Interpolator(WorldLatitudeDeg, WorldTemperatureAutumn);
        }

        public static Interpolator WorldSpringLatitudetoTemperatureC()
        {
            return new Interpolator(WorldLatitudeDeg, WorldTemperatureSpring);
        }

        public static Interpolator WorldSummerLatitudetoTemperatureC()
        {
            return new Interpolator(WorldLatitudeDeg, WorldTemperatureSummer);
        }

        // Auxiliary Water Tenders
        public float MaxAuxTenderWaterMassKG;
        public bool IsAuxTenderCoupled = false;
        bool AuxTenderFound = false;
        string PrevWagonType;


        //To investigate coupler breaks on route
        private bool numOfCouplerBreaksNoted = false;
        public static int NumOfCouplerBreaks = 0;//Debrief Eval
        public bool DbfEvalValueChanged { get;set; }//Debrief Eval

        public enum TRAINTYPE
        {
            PLAYER,
            INTENDED_PLAYER,
            STATIC,
            AI,
            AI_NOTSTARTED,
            AI_AUTOGENERATE,
            REMOTE,
            AI_PLAYERDRIVEN,   //Player is on board and is durrently driving train
            AI_PLAYERHOSTING,   //Player is on board, but train is currently autopiloted
            AI_INCORPORATED    // AI train is incorporated in other train
        }

        public TRAINTYPE TrainType = TRAINTYPE.PLAYER;

        public float? DistanceToSignal = null;
        public List<ObjectItemInfo> SignalObjectItems;
        public int IndexNextSignal = -1;                 // Index in SignalObjectItems for next signal
        public int IndexNextSpeedlimit = -1;             // Index in SignalObjectItems for next speedpost
        public SignalObject[] NextSignalObject = new SignalObject[2];  // direct reference to next signal

        public float TrainMaxSpeedMpS;                   // Max speed as set by route (default value)
        public float AllowedMaxSpeedMpS;                 // Max speed as allowed
        public float allowedMaxSpeedSignalMpS;           // Max speed as set by signal
        public float allowedMaxSpeedLimitMpS;            // Max speed as set by limit
        public float allowedMaxTempSpeedLimitMpS;        // Max speed as set by temp speed limit
        public float allowedAbsoluteMaxSpeedSignalMpS;   // Max speed as set by signal independently from train features
        public float allowedAbsoluteMaxSpeedLimitMpS;    // Max speed as set by limit independently from train features
        public float allowedAbsoluteMaxTempSpeedLimitMpS;    // Max speed as set by temp speed limit independently from train features
        public float maxTimeS = 120;                     // check ahead for distance covered in 2 mins.
        public float minCheckDistanceM = 5000;           // minimum distance to check ahead
        public float minCheckDistanceManualM = 3000;     // minimum distance to check ahead in manual mode

        public float standardOverlapM = 15.0f;           // standard overlap on clearing sections
        public float junctionOverlapM = 75.0f;           // standard overlap on clearing sections
        public float rearPositionOverlap = 25.0f;        // allowed overlap when slipping
        private float standardWaitTimeS = 60.0f;         // wait for 1 min before claim state
        private float backwardThreshold = 20;            // counter threshold to detect backward move

        public Signals signalRef { get; protected set; } // reference to main Signals class: SPA change protected to public with get, set!
        public TCRoutePath TCRoute;                      // train path converted to TC base
        public TCSubpathRoute[] ValidRoute = new TCSubpathRoute[2] { null, null };  // actual valid path
        public TCSubpathRoute TrainRoute;                // partial route under train for Manual mode
        public bool ClaimState;                          // train is allowed to perform claim on sections
        public float actualWaitTimeS;                    // actual time waiting for signal
        public int movedBackward;                        // counter to detect backward move
        public float waitingPointWaitTimeS = -1.0f;      // time due at waiting point (PLAYER train only, valid in >= 0)

        public List<TrackCircuitSection> OccupiedTrack = new List<TrackCircuitSection>();

        // Station Info
        public List<int> HoldingSignals = new List<int>();// list of signals which must not be cleared (eg station stops)
        public List<StationStop> StationStops = new List<StationStop>();  //list of station stop details
        public StationStop PreviousStop = null;                           //last stop passed
        public bool AtStation = false;                                    //set if train is in station
        public bool MayDepart = false;                                    //set if train is ready to depart
        public string DisplayMessage = "";                                //string to be displayed in station information window
        public Color DisplayColor = Color.LightGreen;                     //color for DisplayMessage
        public bool CheckStations = false;                                //used when in timetable mode to check on stations
        public TimeSpan? Delay = null;                                    // present delay of the train (if any)

        public int AttachTo = -1;                              // attach information : train to which to attach at end of run
        public int IncorporatedTrainNo = -1;                        // number of train incorporated in actual train
        public Train IncorporatingTrain;                      // train incorporating another train
        public int IncorporatingTrainNo = -1;                   // number of train incorporating the actual train

        public Traffic_Service_Definition TrafficService;
        public int[,] MisalignedSwitch = new int[2, 2] { { -1, -1 }, { -1, -1 } };  // misaligned switch indication per direction:
        // cell 0 : index of switch, cell 1 : required linked section; -1 if not valid
        public Dictionary<int, float> PassedSignalSpeeds = new Dictionary<int, float>();  // list of signals and related speeds pending processing (manual and explorer mode)
        public int[] LastPassedSignal = new int[2] { -1, -1 };  // index of last signal which set speed limit per direction (manual and explorer mode)

        // Variables used for autopilot mode and played train switching
        public bool IsActualPlayerTrain
        {
            get
            {
                if (Simulator.PlayerLocomotive == null)
                {
                    return false;
                }
                return this == Simulator.PlayerLocomotive.Train;
            }
        }
        public bool IsPlayerDriven
        {
            get
            {
                return (TrainType == TRAINTYPE.PLAYER || TrainType == TRAINTYPE.AI_PLAYERDRIVEN);
            }
        }

        public bool IsPlayable = false;
        public bool IsPathless = false;

        // End variables used for autopilot mode and played train switching

        public TrainRouted routedForward;                 // routed train class for forward moves (used in signalling)
        public TrainRouted routedBackward;                // routed train class for backward moves (used in signalling)

        public enum TRAIN_CONTROL
        {
            AUTO_SIGNAL,
            AUTO_NODE,
            MANUAL,
            EXPLORER,
            OUT_OF_CONTROL,
            INACTIVE,
            TURNTABLE,
            UNDEFINED
        }

        public TRAIN_CONTROL ControlMode = TRAIN_CONTROL.UNDEFINED;     // train control mode

        public enum OUTOFCONTROL
        {
            SPAD,
            SPAD_REAR,
            MISALIGNED_SWITCH,
            OUT_OF_AUTHORITY,
            OUT_OF_PATH,
            SLIPPED_INTO_PATH,
            SLIPPED_TO_ENDOFTRACK,
            OUT_OF_TRACK,
            SLIPPED_INTO_TURNTABLE,
            UNDEFINED
        }

        public OUTOFCONTROL OutOfControlReason = OUTOFCONTROL.UNDEFINED; // train out of control

        public TCPosition[] PresentPosition = new TCPosition[2] { new TCPosition(), new TCPosition() };         // present position : 0 = front, 1 = rear
        public TCPosition[] PreviousPosition = new TCPosition[2] { new TCPosition(), new TCPosition() };        // previous train position

        public float DistanceTravelledM;                                 // actual distance travelled
        public float ReservedTrackLengthM = 0.0f;                        // lenght of reserved section

        public float travelled;                                          // distance travelled, but not exactly
        public float targetSpeedMpS;                                    // target speed for remote trains; used for sound management
        public DistanceTravelledActions requiredActions = new DistanceTravelledActions(); // distance travelled action list
        public AuxActionsContainer AuxActionsContain;          // Action To Do during activity, like WP

        public float activityClearingDistanceM = 30.0f;        // clear distance to stopping point for activities
        public const float shortClearingDistanceM = 15.0f;     // clearing distance for short trains in activities
        public const float standardClearingDistanceM = 30.0f;  // standard clearing distance for trains in activities
        public const int standardTrainMinCarNo = 10;           // Minimum number of cars for a train to have standard clearing distance

        public float ClearanceAtRearM = -1;              // save distance behind train (when moving backward)
        public SignalObject RearSignalObject;            // direct reference to signal at rear (when moving backward)
        public bool IsTilting;

        public float InitialSpeed = 0;                 // initial speed of train in activity as set in .srv file
        public float InitialThrottlepercent = 25; // initial value of throttle when train starts activity at speed > 0

        public double BrakingTime;              // Total braking time, used to check whether brakes get stuck
        public float ContinuousBrakingTime;     // Consecutive braking time, used to check whether brakes get stuck
        public double RunningTime;              // Total running time, used to check whether a locomotive is partly or totally unpowered due to a fault
        public int UnpoweredLoco = -1;          // car index of unpowered loco

        // TODO: Replace this with an event
        public bool FormationReversed;          // flags the execution of the ReverseFormation method (executed at reversal points)

        public enum END_AUTHORITY
        {
            END_OF_TRACK,
            END_OF_PATH,
            RESERVED_SWITCH,
            TRAIN_AHEAD,
            MAX_DISTANCE,
            LOOP,
            SIGNAL,                                       // in Manual mode only
            END_OF_AUTHORITY,                             // when moving backward in Auto mode
            NO_PATH_RESERVED
        }

        public END_AUTHORITY[] EndAuthorityType = new END_AUTHORITY[2] { END_AUTHORITY.NO_PATH_RESERVED, END_AUTHORITY.NO_PATH_RESERVED };

        public int[] LastReservedSection = new int[2] { -1, -1 };         // index of furthest cleared section (for NODE control)
        public float[] DistanceToEndNodeAuthorityM = new float[2];      // distance to end of authority
        public int LoopSection = -1;                                    // section where route loops back onto itself

        public bool nextRouteReady = false;                             // indication to activity.cs that a reversal has taken place

        // Deadlock Info : 
        // list of sections where deadlock begins
        // per section : list with trainno and end section
        public Dictionary<int, List<Dictionary<int, int>>> DeadlockInfo =
            new Dictionary<int, List<Dictionary<int, int>>>();

        // Logging and debugging info
        public bool CheckTrain;                          // debug print required
        private static int lastSpeedLog;                 // last speedlog time
        public bool DatalogTrainSpeed;                   // logging of train speed required
        public int DatalogTSInterval;                    // logging interval
        public int[] DatalogTSContents;                  // logging selection
        public string DataLogFile;                       // required datalog file

        public Simulator Simulator { get; protected set; }                   // reference to the simulator


        // For AI control of the train
        public float AITrainBrakePercent
        {
            get
            {
                return aiBrakePercent;
            }
            set
            {
                aiBrakePercent = value;
                foreach (TrainCar car in Cars)
                    car.BrakeSystem.AISetPercent(aiBrakePercent);
            }
        }
        private float aiBrakePercent;
        public float AITrainThrottlePercent
        {
            get
            {
                return MUThrottlePercent;
            }
            set
            {
                MUThrottlePercent = value;
            }
        }

        public int AITrainGearboxGearIndex
        {
            set
            {
                MUGearboxGearIndex = value;
            }
            get
            {
                return MUGearboxGearIndex;
            }
        }
        public bool AITrainDirectionForward
        {
            get
            {
                return MUDirection == Direction.Forward;
            }
            set
            {
                MUDirection = value ? Direction.Forward : Direction.Reverse;
                MUReverserPercent = value ? 100 : -100;
            }
        }
        public TrainCar LeadLocomotive
        {
            get
            {
                return LeadLocomotiveIndex >= 0 && LeadLocomotiveIndex < Cars.Count ? Cars[LeadLocomotiveIndex] : null;
            }
            set
            {
                LeadLocomotiveIndex = -1;
                for (int i = 0; i < Cars.Count; i++)
                    if (value == Cars[i] && value.IsDriveable)
                    {
                        LeadLocomotiveIndex = i;
                        //MSTSLocomotive lead = (MSTSLocomotive)Cars[LeadLocomotiveIndex];
                        //if (lead.EngineBrakeController != null)
                        //    lead.EngineBrakeController.UpdateEngineBrakePressure(ref BrakeLine3PressurePSI, 1000);
                    }
            }
        }

        // Get the UiD value of the first wagon - searches along train, and gets the integer UiD of the first wagon that is not an engine or tender
        public virtual int GetFirstWagonUiD()
        {
            FirstCarUiD = 0; // Initialise at zero every time routine runs
            foreach (TrainCar car in Cars)
            {
                if (car.WagonType != MSTSWagon.WagonTypes.Engine && car.WagonType != MSTSWagon.WagonTypes.Tender) // If car is not a locomotive or tender, then set UiD
                {
                    FirstCarUiD = car.UiD;
                }
                if (FirstCarUiD != 0)
                {
                    break; // If UiD has been set, then don't go any further
                }
            }
            return FirstCarUiD;
        }

        // Determine whther there are any wagons attached to the locomotive
        public virtual bool GetWagonsAttachedIndication()
        {
            WagonsAttached = false;
             foreach (TrainCar car in Cars)
            {
                // Test to see if freight or passenger wagons attached (used to set BC pressure in locomotive or wagons)
                if (car.WagonType == MSTSWagon.WagonTypes.Freight || car.WagonType == MSTSWagon.WagonTypes.Passenger)
                {
                    WagonsAttached = true;
                    break;
                }
                else
                {
                    WagonsAttached = false;
                }
             }
             return WagonsAttached; 
        }

        //================================================================================================//
        //
        // Constructor
        //

        void Init(Simulator simulator)
        {
            Simulator = simulator;
            allowedAbsoluteMaxSpeedSignalMpS = (float)Simulator.TRK.Tr_RouteFile.SpeedLimit;
            allowedAbsoluteMaxSpeedLimitMpS = allowedAbsoluteMaxSpeedSignalMpS;
            allowedAbsoluteMaxTempSpeedLimitMpS = allowedAbsoluteMaxSpeedSignalMpS;
        }

        public Train(Simulator simulator)
        {
            Init(simulator);

            if (Simulator.IsAutopilotMode && TotalNumber == 1 && Simulator.TrainDictionary.Count == 0) TotalNumber = 0; //The autopiloted train has number 0
            Number = TotalNumber;
            TotalNumber++;
            SignalObjectItems = new List<ObjectItemInfo>();
            signalRef = simulator.Signals;
            Name = "";

            routedForward = new TrainRouted(this, 0);
            routedBackward = new TrainRouted(this, 1);
            AuxActionsContain = new AuxActionsContainer(this, Simulator.orRouteConfig);
        }

        //================================================================================================//
        //
        // Constructor for Dummy entries used on restore
        // Signals is restored before Trains, links are restored by Simulator
        //

        public Train(Simulator simulator, int number)
        {
            Init(simulator);
            Number = number;
            routedForward = new TrainRouted(this, 0);
            routedBackward = new TrainRouted(this, 1);
            AuxActionsContain = new AuxActionsContainer(this, null);
        }

        //================================================================================================//
        //
        // Constructor for uncoupled trains
        // copy path info etc. from original train
        //

        public Train(Simulator simulator, Train orgTrain)
        {
            Init(simulator);
            Number = TotalNumber;
            Name = String.Concat(String.Copy(orgTrain.Name), TotalNumber.ToString());
            TotalNumber++;
            SignalObjectItems = new List<ObjectItemInfo>();
            signalRef = simulator.Signals;

            AuxActionsContain = new AuxActionsContainer(this, Simulator.orRouteConfig);
            if (orgTrain.TrafficService != null)
            {
                TrafficService = new Traffic_Service_Definition();
                TrafficService.Time = orgTrain.TrafficService.Time;

                foreach (Traffic_Traffic_Item thisTrafficItem in orgTrain.TrafficService.TrafficDetails)
                {
                    TrafficService.TrafficDetails.Add(thisTrafficItem);
                }
            }

            if (orgTrain.TCRoute != null)
            {
                TCRoute = new TCRoutePath(orgTrain.TCRoute);
            }

            ValidRoute[0] = new TCSubpathRoute(orgTrain.ValidRoute[0]);
            ValidRoute[1] = new TCSubpathRoute(orgTrain.ValidRoute[1]);

            DistanceTravelledM = orgTrain.DistanceTravelledM;

            if (orgTrain.requiredActions.Count > 0)
            {
                requiredActions = orgTrain.requiredActions.Copy();
            }

            routedForward = new TrainRouted(this, 0);
            routedBackward = new TrainRouted(this, 1);

            ControlMode = orgTrain.ControlMode;

            AllowedMaxSpeedMpS = orgTrain.AllowedMaxSpeedMpS;
            allowedMaxSpeedLimitMpS = orgTrain.allowedMaxSpeedLimitMpS;
            allowedMaxSpeedSignalMpS = orgTrain.allowedMaxSpeedSignalMpS;
            allowedAbsoluteMaxSpeedLimitMpS = orgTrain.allowedAbsoluteMaxSpeedLimitMpS;
            allowedAbsoluteMaxSpeedSignalMpS = orgTrain.allowedAbsoluteMaxSpeedSignalMpS;

            if (orgTrain.StationStops != null)
            {
                foreach (StationStop thisStop in orgTrain.StationStops)
                {
                    StationStop newStop = thisStop.CreateCopy();
                    StationStops.Add(newStop);
                }
            }
            else
            {
                StationStops = null;
            }

        }

        //================================================================================================//
        /// <summary>
        /// Restore
        /// <\summary>

        public Train(Simulator simulator, BinaryReader inf)
        {
            Init(simulator);

            routedForward = new TrainRouted(this, 0);
            routedBackward = new TrainRouted(this, 1);

            RestoreCars(simulator, inf);
            Number = inf.ReadInt32();
            TotalNumber = Math.Max(Number + 1, TotalNumber);
            Name = inf.ReadString();
            SpeedMpS = inf.ReadSingle();
            TrainCurrentCarriageHeatTempC = inf.ReadSingle();
            TrainCurrentTrainSteamHeatW = inf.ReadSingle();
            TrainType = (TRAINTYPE)inf.ReadInt32();
            MUDirection = (Direction)inf.ReadInt32();
            MUThrottlePercent = inf.ReadSingle();
            MUGearboxGearIndex = inf.ReadInt32();
            MUDynamicBrakePercent = inf.ReadSingle();
            EqualReservoirPressurePSIorInHg = inf.ReadSingle();
            BrakeLine2PressurePSI = inf.ReadSingle();
            BrakeLine3PressurePSI = inf.ReadSingle();
            BrakeLine4 = inf.ReadSingle();
            aiBrakePercent = inf.ReadSingle();
            LeadLocomotiveIndex = inf.ReadInt32();
            RetainerSetting = (RetainerSetting)inf.ReadInt32();
            RetainerPercent = inf.ReadInt32();
            RearTDBTraveller = new Traveller(simulator.TSectionDat, simulator.TDB.TrackDB.TrackNodes, inf);
            SlipperySpotDistanceM = inf.ReadSingle();
            SlipperySpotLengthM = inf.ReadSingle();
            TrainMaxSpeedMpS = inf.ReadSingle();
            AllowedMaxSpeedMpS = inf.ReadSingle();
            allowedMaxSpeedSignalMpS = inf.ReadSingle();
            allowedMaxSpeedLimitMpS = inf.ReadSingle();
            allowedMaxTempSpeedLimitMpS = inf.ReadSingle();
            allowedAbsoluteMaxSpeedSignalMpS = inf.ReadSingle();
            allowedAbsoluteMaxSpeedLimitMpS = inf.ReadSingle();
            allowedAbsoluteMaxTempSpeedLimitMpS = inf.ReadSingle();
            BrakingTime = inf.ReadDouble();
            ContinuousBrakingTime = inf.ReadSingle();
            RunningTime = inf.ReadDouble();
            IncorporatedTrainNo = inf.ReadInt32();
            IncorporatingTrainNo = inf.ReadInt32();
            IsAuxTenderCoupled = inf.ReadBoolean();
            if (IncorporatedTrainNo > -1)
            {
                Train train = GetOtherTrainByNumber(IncorporatedTrainNo);
                if (train != null)
                {
                    train.IncorporatingTrain = this;
                    train.IncorporatingTrainNo = Number;
                }
            }
            if (IncorporatingTrainNo > -1)
            {
                Train train = GetOtherTrainByNumber(IncorporatingTrainNo);
                if (train != null)
                {
                    IncorporatingTrain = train;
                }
            }
            CheckFreight();


            SignalObjectItems = new List<ObjectItemInfo>();
            signalRef = simulator.Signals;

            TrainType = (TRAINTYPE)inf.ReadInt32();
            IsTilting = inf.ReadBoolean();
            ClaimState = inf.ReadBoolean();
            DatalogTrainSpeed = inf.ReadBoolean();
            DatalogTSInterval = inf.ReadInt32();
            int dslenght = inf.ReadInt32();
            if (dslenght > 0)
            {
                DatalogTSContents = new int[dslenght];
                for (int iDs = 0; iDs <= dslenght - 1; iDs++)
                {
                    DatalogTSContents[iDs] = inf.ReadInt32();
                }
            }
            else
            {
                DatalogTSContents = null;
            }

            int dsfile = inf.ReadInt32();
            if (dsfile < 0)
            {
                DataLogFile = String.Empty;
            }
            else
            {
                DataLogFile = inf.ReadString();
            }

            TCRoute = null;
            bool routeAvailable = inf.ReadBoolean();
            if (routeAvailable)
            {
                TCRoute = new TCRoutePath(inf);
            }

            ValidRoute[0] = null;
            bool validRouteAvailable = inf.ReadBoolean();
            if (validRouteAvailable)
            {
                ValidRoute[0] = new TCSubpathRoute(inf);
            }

            ValidRoute[1] = null;
            validRouteAvailable = inf.ReadBoolean();
            if (validRouteAvailable)
            {
                ValidRoute[1] = new TCSubpathRoute(inf);
            }

            int totalOccTrack = inf.ReadInt32();
            for (int iTrack = 0; iTrack < totalOccTrack; iTrack++)
            {
                int sectionIndex = inf.ReadInt32();
                TrackCircuitSection thisSection = signalRef.TrackCircuitList[sectionIndex];
                OccupiedTrack.Add(thisSection);
            }

            int totalHoldSignals = inf.ReadInt32();
            for (int iSignal = 0; iSignal < totalHoldSignals; iSignal++)
            {
                int thisHoldSignal = inf.ReadInt32();
                HoldingSignals.Add(thisHoldSignal);
            }

            int totalStations = inf.ReadInt32();
            for (int iStation = 0; iStation < totalStations; iStation++)
            {
                StationStop thisStation = new StationStop(inf, signalRef);
                StationStops.Add(thisStation);
            }

            int prevStopAvail = inf.ReadInt32();
            if (prevStopAvail >= 0)
            {
                PreviousStop = new StationStop(inf, signalRef);
            }
            else
            {
                PreviousStop = null;
            }

            AtStation = inf.ReadBoolean();
            MayDepart = inf.ReadBoolean();
            CheckStations = inf.ReadBoolean();
            AttachTo = inf.ReadInt32();

            DisplayMessage = inf.ReadString();

            int DelaySeconds = inf.ReadInt32();
            if (DelaySeconds < 0) // delay value (in seconds, as integer)
            {
                Delay = null;
            }
            else
            {
                Delay = TimeSpan.FromSeconds(DelaySeconds);
            }

            int totalPassedSignals = inf.ReadInt32();
            for (int iPassedSignal = 0; iPassedSignal < totalPassedSignals; iPassedSignal++)
            {
                int passedSignalKey = inf.ReadInt32();
                float passedSignalValue = inf.ReadSingle();
                PassedSignalSpeeds.Add(passedSignalKey, passedSignalValue);
            }
            LastPassedSignal[0] = inf.ReadInt32();
            LastPassedSignal[1] = inf.ReadInt32();

            bool trafficServiceAvailable = inf.ReadBoolean();
            if (trafficServiceAvailable)
            {
                TrafficService = RestoreTrafficSDefinition(inf);
            }

            ControlMode = (TRAIN_CONTROL)inf.ReadInt32();
            OutOfControlReason = (OUTOFCONTROL)inf.ReadInt32();
            EndAuthorityType[0] = (END_AUTHORITY)inf.ReadInt32();
            EndAuthorityType[1] = (END_AUTHORITY)inf.ReadInt32();
            LastReservedSection[0] = inf.ReadInt32();
            LastReservedSection[1] = inf.ReadInt32();
            LoopSection = inf.ReadInt32();
            DistanceToEndNodeAuthorityM[0] = inf.ReadSingle();
            DistanceToEndNodeAuthorityM[1] = inf.ReadSingle();

            if (TrainType != TRAINTYPE.AI_NOTSTARTED && TrainType != TRAINTYPE.AI_AUTOGENERATE)
            {
                CalculatePositionOfCars();

                DistanceTravelledM = inf.ReadSingle();
                PresentPosition[0] = new TCPosition();
                PresentPosition[0].RestorePresentPosition(inf, this);
                PresentPosition[1] = new TCPosition();
                PresentPosition[1].RestorePresentRear(inf, this);
                PreviousPosition[0] = new TCPosition();
                PreviousPosition[0].RestorePreviousPosition(inf);

                PresentPosition[0].RouteListIndex = ValidRoute[0].GetRouteIndex(PresentPosition[0].TCSectionIndex, 0);
                PresentPosition[1].RouteListIndex = ValidRoute[0].GetRouteIndex(PresentPosition[1].TCSectionIndex, 0);
            }
            else
            {
                DistanceTravelledM = inf.ReadSingle();
                PresentPosition[0] = new TCPosition();
                PresentPosition[0].RestorePresentPositionDummy(inf, this);
                PresentPosition[1] = new TCPosition();
                PresentPosition[1].RestorePresentRearDummy(inf, this);
                PreviousPosition[0] = new TCPosition();
                PreviousPosition[0].RestorePreviousPositionDummy(inf);
            }
            travelled = DistanceTravelledM;
            int activeActions = inf.ReadInt32();
            for (int iAction = 0; iAction < activeActions; iAction++)
            {
                int actionType = inf.ReadInt32();
                switch (actionType)
                {
                    case 1:
                        ActivateSpeedLimit speedLimit = new ActivateSpeedLimit(inf);
                        requiredActions.InsertAction(speedLimit);
                        break;
                    case 2:
                        ClearSectionItem clearSection = new ClearSectionItem(inf);
                        requiredActions.InsertAction(clearSection);
                        break;
                    case 3:
                        AIActionItem actionItem = new AIActionItem(inf, signalRef);
                        requiredActions.InsertAction(actionItem);
                        break;
                    case 4:
                        AuxActionItem auxAction = new AuxActionItem(inf, signalRef);
                        requiredActions.InsertAction(auxAction);
                        Trace.TraceWarning("DistanceTravelledItem type 4 restored as AuxActionItem");
                        break;
                    default:
                        Trace.TraceWarning("Unknown type of DistanceTravelledItem (type {0}",
                                actionType.ToString());
                        break;
                }
            }

            AuxActionsContain = new AuxActionsContainer(this, inf, Simulator.RoutePath);
            RestoreDeadlockInfo(inf);

            InitialSpeed = inf.ReadSingle();
            IsPathless = inf.ReadBoolean();

            if (TrainType != TRAINTYPE.REMOTE)
            {
                // restore leadlocomotive
                if (LeadLocomotiveIndex >= 0)
                {
                    LeadLocomotive = Cars[LeadLocomotiveIndex];
                    if (TrainType != TRAINTYPE.STATIC)
                        Simulator.PlayerLocomotive = LeadLocomotive;
                }

                // restore logfile
                if (DatalogTrainSpeed)
                {
                    CreateLogFile();
                }
            }
        }

        private void RestoreCars(Simulator simulator, BinaryReader inf)
        {
            int count = inf.ReadInt32();
            if (count > 0)
            {
                for (int i = 0; i < count; ++i)
                    Cars.Add(RollingStock.Restore(simulator, inf, this));
            }
        }

        static Traffic_Service_Definition RestoreTrafficSDefinition(BinaryReader inf)
        {
            Traffic_Service_Definition thisDefinition = new Traffic_Service_Definition();
            thisDefinition.Time = inf.ReadInt32();

            int totalTrafficItems = inf.ReadInt32();

            for (int iTraffic = 0; iTraffic < totalTrafficItems; iTraffic++)
            {
                Traffic_Traffic_Item thisItem = RestoreTrafficItem(inf);
                thisDefinition.TrafficDetails.Add(thisItem);
            }

            return (thisDefinition);
        }

        static Traffic_Traffic_Item RestoreTrafficItem(BinaryReader inf)
        {
            Traffic_Traffic_Item thisTraffic = new Traffic_Traffic_Item();
            thisTraffic.ArrivalTime = inf.ReadInt32();
            thisTraffic.DepartTime = inf.ReadInt32();
            thisTraffic.DistanceDownPath = inf.ReadSingle();
            thisTraffic.PlatformStartID = inf.ReadInt32();

            return (thisTraffic);
        }

        private void RestoreDeadlockInfo(BinaryReader inf)
        {
            int totalDeadlock = inf.ReadInt32();
            for (int iDeadlockList = 0; iDeadlockList < totalDeadlock; iDeadlockList++)
            {
                int deadlockListKey = inf.ReadInt32();
                int deadlockListLength = inf.ReadInt32();

                List<Dictionary<int, int>> thisDeadlockList = new List<Dictionary<int, int>>();

                for (int iDeadlock = 0; iDeadlock < deadlockListLength; iDeadlock++)
                {
                    int deadlockInfoLength = inf.ReadInt32();
                    Dictionary<int, int> thisDeadlockDetails = new Dictionary<int, int>();

                    for (int iDeadlockDetails = 0; iDeadlockDetails < deadlockInfoLength; iDeadlockDetails++)
                    {
                        int deadlockKey = inf.ReadInt32();
                        int deadlockValue = inf.ReadInt32();

                        thisDeadlockDetails.Add(deadlockKey, deadlockValue);
                    }

                    thisDeadlockList.Add(thisDeadlockDetails);
                }
                DeadlockInfo.Add(deadlockListKey, thisDeadlockList);
            }
        }


        //================================================================================================//
        /// <summary>
        /// save game state
        /// <\summary>

        public virtual void Save(BinaryWriter outf)
        {
            SaveCars(outf);
            outf.Write(Number);
            outf.Write(Name);
            outf.Write(SpeedMpS);
            outf.Write(TrainCurrentCarriageHeatTempC);
            outf.Write(TrainCurrentTrainSteamHeatW);
            outf.Write((int)TrainType);
            outf.Write((int)MUDirection);
            outf.Write(MUThrottlePercent);
            outf.Write(MUGearboxGearIndex);
            outf.Write(MUDynamicBrakePercent);
            outf.Write(EqualReservoirPressurePSIorInHg);
            outf.Write(BrakeLine2PressurePSI);
            outf.Write(BrakeLine3PressurePSI);
            outf.Write(BrakeLine4);
            outf.Write(aiBrakePercent);
            outf.Write(LeadLocomotiveIndex);
            outf.Write((int)RetainerSetting);
            outf.Write(RetainerPercent);
            RearTDBTraveller.Save(outf);
            outf.Write(SlipperySpotDistanceM);
            outf.Write(SlipperySpotLengthM);
            outf.Write(TrainMaxSpeedMpS);
            outf.Write(AllowedMaxSpeedMpS);
            outf.Write(allowedMaxSpeedSignalMpS);
            outf.Write(allowedMaxSpeedLimitMpS);
            outf.Write(allowedMaxTempSpeedLimitMpS);
            outf.Write(allowedAbsoluteMaxSpeedSignalMpS);
            outf.Write(allowedAbsoluteMaxSpeedLimitMpS);
            outf.Write(allowedAbsoluteMaxTempSpeedLimitMpS);
            outf.Write(BrakingTime);
            outf.Write(ContinuousBrakingTime);
            outf.Write(RunningTime);
            outf.Write(IncorporatedTrainNo);
            outf.Write(IncorporatingTrainNo);
            outf.Write(IsAuxTenderCoupled);

            outf.Write((int)TrainType);
            outf.Write(IsTilting);
            outf.Write(ClaimState);
            outf.Write(DatalogTrainSpeed);
            outf.Write(DatalogTSInterval);

            if (DatalogTSContents == null)
            {
                outf.Write(-1);
            }
            else
            {
                outf.Write(DatalogTSContents.Length);
                foreach (int dselect in DatalogTSContents)
                {
                    outf.Write(dselect);
                }
            }

            if (String.IsNullOrEmpty(DataLogFile))
            {
                outf.Write(-1);
            }
            else
            {
                outf.Write(1);
                outf.Write(DataLogFile);
            }

            if (TCRoute == null)
            {
                outf.Write(false);
            }
            else
            {
                outf.Write(true);
                TCRoute.Save(outf);
            }

            if (ValidRoute[0] == null)
            {
                outf.Write(false);
            }
            else
            {
                outf.Write(true);
                ValidRoute[0].Save(outf);
            }

            if (ValidRoute[1] == null)
            {
                outf.Write(false);
            }
            else
            {
                outf.Write(true);
                ValidRoute[1].Save(outf);
            }

            outf.Write(OccupiedTrack.Count);
            foreach (TrackCircuitSection thisSection in OccupiedTrack)
            {
                outf.Write(thisSection.Index);
            }

            outf.Write(HoldingSignals.Count);
            foreach (int thisHold in HoldingSignals)
            {
                outf.Write(thisHold);
            }

            outf.Write(StationStops.Count);
            foreach (StationStop thisStop in StationStops)
            {
                thisStop.Save(outf);
            }

            if (PreviousStop == null)
            {
                outf.Write(-1);
            }
            else
            {
                outf.Write(1);
                PreviousStop.Save(outf);
            }

            outf.Write(AtStation);
            outf.Write(MayDepart);
            outf.Write(CheckStations);
            outf.Write(AttachTo);

            outf.Write(DisplayMessage);

            int DelaySeconds = Delay.HasValue ? (int)Delay.Value.TotalSeconds : -1;
            outf.Write(DelaySeconds);

            outf.Write(PassedSignalSpeeds.Count);
            foreach (KeyValuePair<int, float> thisPair in PassedSignalSpeeds)
            {
                outf.Write(thisPair.Key);
                outf.Write(thisPair.Value);
            }
            outf.Write(LastPassedSignal[0]);
            outf.Write(LastPassedSignal[1]);

            if (TrafficService == null)
            {
                outf.Write(false);
            }
            else
            {
                outf.Write(true);
                SaveTrafficSDefinition(outf, TrafficService);
            }

            outf.Write((int)ControlMode);
            outf.Write((int)OutOfControlReason);
            outf.Write((int)EndAuthorityType[0]);
            outf.Write((int)EndAuthorityType[1]);
            outf.Write(LastReservedSection[0]);
            outf.Write(LastReservedSection[1]);
            outf.Write(LoopSection);
            outf.Write(DistanceToEndNodeAuthorityM[0]);
            outf.Write(DistanceToEndNodeAuthorityM[1]);

            outf.Write(DistanceTravelledM);
            PresentPosition[0].Save(outf);
            PresentPosition[1].Save(outf);
            PreviousPosition[0].Save(outf);
            //  Save requiredAction, the original actions
            outf.Write(requiredActions.Count);
            foreach (DistanceTravelledItem thisAction in requiredActions)
            {
                thisAction.Save(outf);
            }
            //  Then, save the Auxiliary Action Container
            SaveAuxContainer(outf);

            SaveDeadlockInfo(outf);
            // Save initial speed
            outf.Write(InitialSpeed);
            outf.Write(IsPathless);
        }

        private void SaveCars(BinaryWriter outf)
        {
            outf.Write(Cars.Count);
            foreach (TrainCar car in Cars)
                RollingStock.Save(outf, car);
        }

        static void SaveTrafficSDefinition(BinaryWriter outf, Traffic_Service_Definition thisTSD)
        {
            outf.Write(thisTSD.Time);
            outf.Write(thisTSD.TrafficDetails.Count);
            foreach (Traffic_Traffic_Item thisTI in thisTSD.TrafficDetails)
            {
                SaveTrafficItem(outf, thisTI);
            }
        }

        static void SaveTrafficItem(BinaryWriter outf, Traffic_Traffic_Item thisTI)
        {
            outf.Write(thisTI.ArrivalTime);
            outf.Write(thisTI.DepartTime);
            outf.Write(thisTI.DistanceDownPath);
            outf.Write(thisTI.PlatformStartID);
        }

        private void SaveDeadlockInfo(BinaryWriter outf)
        {
            outf.Write(DeadlockInfo.Count);
            foreach (KeyValuePair<int, List<Dictionary<int, int>>> thisInfo in DeadlockInfo)
            {
                outf.Write(thisInfo.Key);
                outf.Write(thisInfo.Value.Count);

                foreach (Dictionary<int, int> thisDeadlock in thisInfo.Value)
                {
                    outf.Write(thisDeadlock.Count);
                    foreach (KeyValuePair<int, int> thisDeadlockDetails in thisDeadlock)
                    {
                        outf.Write(thisDeadlockDetails.Key);
                        outf.Write(thisDeadlockDetails.Value);
                    }
                }
            }
        }

        private void SaveAuxContainer(BinaryWriter outf)
        {
            AuxActionsContain.Save(outf, Convert.ToInt32(Math.Floor(Simulator.ClockTime)));
        }


        //================================================================================================//
        /// <summary>
        /// Changes the Lead locomotive (i.e. the loco which the player controls) to the next in the consist.
        /// Steps back through the train, ignoring any cabs that face rearwards until there are no forward-facing
        /// cabs left. Then continues from the rearmost, rearward-facing cab, reverses the train and resumes stepping back.
        /// E.g. if consist is made of 3 cars, each with front and rear-facing cabs
        ///     (A-b]:(C-d]:[e-F)
        /// then pressing Ctrl+E cycles the cabs in the sequence
        ///     A -> b -> C -> d -> e -> F
        /// </summary>
        public TrainCar GetNextCab()
        {
            // negative numbers used if rear cab selected
            // because '0' has no negative, all indices are shifted by 1!!!!

            int presentIndex = LeadLocomotiveIndex + 1;
            if (((MSTSLocomotive)LeadLocomotive).UsingRearCab) presentIndex = -presentIndex;

            List<int> cabList = new List<int>();

            for (int i = 0; i < Cars.Count; i++)
            {
                if (SkipOtherUsersCar(i)) continue;
                var cab3d = Cars[i].HasFront3DCab || Cars[i].HasRear3DCab;
                var hasFrontCab = cab3d ? Cars[i].HasFront3DCab : Cars[i].HasFrontCab;
                var hasRearCab = cab3d ? Cars[i].HasRear3DCab : Cars[i].HasRearCab;
                if (Cars[i].Flipped)
                {
                    if (hasRearCab) cabList.Add(-(i + 1));
                    if (hasFrontCab) cabList.Add(i + 1);
                }
                else
                {
                    if (hasFrontCab) cabList.Add(i + 1);
                    if (hasRearCab) cabList.Add(-(i + 1));
                }
            }

            int lastIndex = cabList.IndexOf(presentIndex);
            if (lastIndex >= cabList.Count - 1) lastIndex = -1;

            int nextCabIndex = cabList[lastIndex + 1];

            TrainCar oldLead = LeadLocomotive;
            LeadLocomotiveIndex = Math.Abs(nextCabIndex) - 1;
            Trace.Assert(LeadLocomotive != null, "Tried to switch to non-existent loco");
            TrainCar newLead = LeadLocomotive;  // Changing LeadLocomotiveIndex also changed LeadLocomotive
            ((MSTSLocomotive)newLead).UsingRearCab = nextCabIndex < 0;

            if (oldLead != null && newLead != null && oldLead != newLead)
            {
                newLead.CopyControllerSettings(oldLead);
                // TODO :: need to link HeadOut cameras to new lead locomotive
                // following should do it but cannot be used due to protection level
                // Program.Viewer.HeadOutBackCamera.SetCameraCar(Cars[LeadLocomotiveIndex]);
                // seems there is nothing to attach camera to car
            }

            // If there is a player locomotive, and it is in this train, update it to match the new lead locomotive.
            if (Simulator.PlayerLocomotive != null && Simulator.PlayerLocomotive.Train == this)

                Simulator.PlayerLocomotive = newLead;

            return newLead;
        }

        //this function is needed for Multiplayer games as they do not need to have cabs, but need to know lead locomotives
        // Sets the Lead locomotive to the next in the consist
        public void LeadNextLocomotive()
        {
            // First driveable
            int firstLead = -1;
            // Next driveale to the current
            int nextLead = -1;
            // Count of driveable locos
            int coud = 0;

            for (int i = 0; i < Cars.Count; i++)
            {
                if (Cars[i].IsDriveable)
                {
                    // Count the driveables
                    coud++;

                    // Get the first driveable
                    if (firstLead == -1)
                        firstLead = i;

                    // If later than current select the next
                    if (LeadLocomotiveIndex < i && nextLead == -1)
                    {
                        nextLead = i;
                    }
                }
            }

            TrainCar prevLead = LeadLocomotive;

            // If found one after the current
            if (nextLead != -1)
                LeadLocomotiveIndex = nextLead;
            // If not, and have more than one, set the first
            else if (coud > 1)
                LeadLocomotiveIndex = firstLead;
            TrainCar newLead = LeadLocomotive;
            if (prevLead != null && newLead != null && prevLead != newLead)
                newLead.CopyControllerSettings(prevLead);
        }

        //================================================================================================//
        /// <summary>
        /// Is there another cab in the player's train to change to?
        /// </summary>
        public bool IsChangeCabAvailable()
        {
            Trace.Assert(Simulator.PlayerLocomotive != null, "Player loco is null when trying to switch locos");
            Trace.Assert(Simulator.PlayerLocomotive.Train == this, "Trying to switch locos but not on player's train");

            int driveableCabs = 0;
            for (int i = 0; i < Cars.Count; i++)
            {
                if (SkipOtherUsersCar(i)) continue;
                if (Cars[i].HasFrontCab || Cars[i].HasFront3DCab) driveableCabs++;
                if (Cars[i].HasRearCab || Cars[i].HasRear3DCab) driveableCabs++;
            }
            if (driveableCabs < 2)
            {
                Simulator.Confirmer.Warning(CabControl.ChangeCab, CabSetting.Warn1);
                return false;
            }
            return true;
        }

        //================================================================================================//
        /// <summary>
        /// In multiplayer, don't want to switch to a locomotive which is player locomotive of another user
        /// </summary>
        /// <param name="i"></param>
        /// <returns></returns>
        private bool SkipOtherUsersCar(int i)
        {
            if (!MPManager.IsMultiPlayer()) return false;
            else
            {
                var thisUsername = MPManager.GetUserName();
                var skip = false;
                foreach (OnlinePlayer onlinePlayer in MPManager.OnlineTrains.Players.Values)
                {
                    // don't consider the present user
                    if (onlinePlayer.Username == thisUsername) continue;
                    if (onlinePlayer.LeadingLocomotiveID == Cars[i].CarID)
                    {
                        skip = true;
                        break;
                    }
                }
                return skip;
            } 
        }

        //================================================================================================//
        /// <summary>
        /// Flips the train if necessary so that the train orientation matches the lead locomotive cab direction
        /// </summary>

        //       public void Orient()
        //       {
        //           TrainCar lead = LeadLocomotive;
        //           if (lead == null || !(lead.Flipped ^ lead.GetCabFlipped()))
        //               return;
        //
        //           if (ControlMode == TRAIN_CONTROL.AUTO_SIGNAL || ControlMode == TRAIN_CONTROL.AUTO_NODE || ControlMode == TRAIN_CONTROL.MANUAL)
        //               return;
        //
        //           for (int i = Cars.Count - 1; i > 0; i--)
        //               Cars[i].CopyCoupler(Cars[i - 1]);
        //           for (int i = 0; i < Cars.Count / 2; i++)
        //           {
        //               int j = Cars.Count - i - 1;
        //               TrainCar car = Cars[i];
        //               Cars[i] = Cars[j];
        //               Cars[j] = car;
        //           }
        //           if (LeadLocomotiveIndex >= 0)
        //               LeadLocomotiveIndex = Cars.Count - LeadLocomotiveIndex - 1;
        //           for (int i = 0; i < Cars.Count; i++)
        //               Cars[i].Flipped = !Cars[i].Flipped;
        //
        //           Traveller t = FrontTDBTraveller;
        //           FrontTDBTraveller = new Traveller(RearTDBTraveller, Traveller.TravellerDirection.Backward);
        //           RearTDBTraveller = new Traveller(t, Traveller.TravellerDirection.Backward);
        //
        //           MUDirection = DirectionControl.Flip(MUDirection);
        //           MUReverserPercent = -MUReverserPercent;
        //       }

        //================================================================================================//
        /// <summary>
        /// Reverse train formation
        /// Only performed when train activates a reversal point
        /// NOTE : this routine handles the physical train orientation only, all related route settings etc. must be handled separately
        /// </summary>

        public void ReverseFormation(bool setMUParameters)
        {
            if (MPManager.IsMultiPlayer()) MPManager.BroadCast((new MSGFlip(this, setMUParameters, Number)).ToString()); // message contains data before flip
            ReverseCars();
            // Flip the train's travellers.
            var t = FrontTDBTraveller;
            FrontTDBTraveller = new Traveller(RearTDBTraveller, Traveller.TravellerDirection.Backward);
            RearTDBTraveller = new Traveller(t, Traveller.TravellerDirection.Backward);
            // If we are updating the controls...
            if (setMUParameters)
            {
                // Flip the controls.
                MUDirection = DirectionControl.Flip(MUDirection);
                MUReverserPercent = -MUReverserPercent;
            }
            if (!((this is AITrain && (this as AITrain).AI.PreUpdate) || this.TrainType == TRAINTYPE.STATIC)) FormationReversed = true;
        }

        //================================================================================================//
        /// <summary>
        /// Reverse cars and car order
        /// </summary>
        /// 

        public void ReverseCars()
        {
            // Shift all the coupler data along the train by 1 car.
            for (var i = Cars.Count - 1; i > 0; i--)
                Cars[i].CopyCoupler(Cars[i - 1]);
            // Reverse brake hose connections and angle cocks
            for (var i = 0; i < Cars.Count; i++)
            {
                var ac = Cars[i].BrakeSystem.AngleCockAOpen;
                Cars[i].BrakeSystem.AngleCockAOpen = Cars[i].BrakeSystem.AngleCockBOpen;
                Cars[i].BrakeSystem.AngleCockBOpen = ac;
                if (i == Cars.Count - 1)
                    Cars[i].BrakeSystem.FrontBrakeHoseConnected = false;
                else
                    Cars[i].BrakeSystem.FrontBrakeHoseConnected = Cars[i + 1].BrakeSystem.FrontBrakeHoseConnected;
            }
            // Reverse the actual order of the cars in the train.
            Cars.Reverse();
            // Update leading locomotive index.
            if (LeadLocomotiveIndex >= 0)
                LeadLocomotiveIndex = Cars.Count - LeadLocomotiveIndex - 1;
            // Update flipped state of each car.
            for (var i = 0; i < Cars.Count; i++)
                Cars[i].Flipped = !Cars[i].Flipped;
        }



        //================================================================================================//
        /// <summary>
        /// Someone is sending an event notification to all cars on this train.
        /// ie doors open, pantograph up, lights on etc.
        /// </summary>

        public void SignalEvent(Event evt)
        {
            foreach (TrainCar car in Cars)
                car.SignalEvent(evt);
        }

        public void SignalEvent(PowerSupplyEvent evt)
        {
            foreach (TrainCar car in Cars)
                car.SignalEvent(evt);
        }

        public void SignalEvent(PowerSupplyEvent evt, int id)
        {
            foreach (TrainCar car in Cars)
                car.SignalEvent(evt, id);
        }

        //================================================================================================//
        /// <summary>
        /// Set starting conditions when speed > 0 
        /// <\summary>

        public virtual void InitializeMoving()
        {
            SpeedMpS = InitialSpeed;
            MUDirection = Direction.Forward;
            float initialThrottlepercent = InitialThrottlepercent;
            MUDynamicBrakePercent = -1;
            //            aiBrakePercent = 0;
            //            AITrainBrakePercent = 0;

            if (LeadLocomotiveIndex >= 0)
            {
                MSTSLocomotive lead = (MSTSLocomotive)Cars[LeadLocomotiveIndex];
                if (lead is MSTSSteamLocomotive) MUReverserPercent = 25;
                lead.CurrentElevationPercent = 100f * lead.WorldPosition.XNAMatrix.M32;

                //TODO: next if block has been inserted to flip trainset physics in order to get viewing direction coincident with loco direction when using rear cab.
                // To achieve the same result with other means, without flipping trainset physics, the block should be deleted
                //         
                if (lead.IsDriveable && (lead as MSTSLocomotive).UsingRearCab)
                {
                    lead.CurrentElevationPercent = -lead.CurrentElevationPercent;
                }
                // give it a bit more gas if it is uphill
                if (lead.CurrentElevationPercent < -2.0) initialThrottlepercent = 40f;
                // better block gas if it is downhill
                else if (lead.CurrentElevationPercent > 1.0) initialThrottlepercent = 0f;

                if (lead.TrainBrakeController != null)
                {
                    EqualReservoirPressurePSIorInHg = lead.TrainBrakeController.MaxPressurePSI;
                }
            }
            MUThrottlePercent = initialThrottlepercent;
            AITrainThrottlePercent = initialThrottlepercent;

            TraincarsInitializeMoving();
        }

        //================================================================================================//
        /// <summary>
        /// Set starting conditions for TrainCars when speed > 0 
        /// <\summary>

        public void TraincarsInitializeMoving()
        {
            for (int i = 0; i < Cars.Count; ++i)
            {
                TrainCar car = Cars[i];
                car.InitializeMoving();
            }
        }

        //================================================================================================//
        /// <summary>
        /// Update train 
        /// <\summary>

        public virtual void Update(float elapsedClockSeconds, bool auxiliaryUpdate = true)
        {
            if (!auxiliaryUpdate)
                FormationReversed = false;
            if (IsActualPlayerTrain && Simulator.ActiveMovingTable != null)
                Simulator.ActiveMovingTable.CheckTrainOnMovingTable(this);

            if (IsActualPlayerTrain && Simulator.OriginalPlayerTrain != this && !CheckStations) // if player train is to check own stations
            {
                CheckStationTask();
            }


            if (IsActualPlayerTrain && Simulator.Settings.Autopilot && Simulator.Settings.ActRandomizationLevel > 0 && Simulator.ActivityRun != null) // defects might occur
            {
                CheckFailures (elapsedClockSeconds);
            }

            // Update train physics, position and movement

            physicsUpdate(elapsedClockSeconds);

            // Update the UiD of First Wagon
            FirstCarUiD = GetFirstWagonUiD();

            // Check to see if wagons are attached to train
            WagonsAttached = GetWagonsAttachedIndication();

            //Exit here when train is static consist (no further actions required)

            if (GetAIMovementState() == AITrain.AI_MOVEMENT_STATE.AI_STATIC)
            {
                int presentTime = Convert.ToInt32(Math.Floor(Simulator.ClockTime));
                UpdateAIStaticState(presentTime);
            }

            if (TrainType == TRAINTYPE.STATIC)
                return;

            // perform overall update

            if (ControlMode == TRAIN_CONTROL.TURNTABLE)
            {
                UpdateTurntable(elapsedClockSeconds);
            }

            else if (ControlMode == TRAIN_CONTROL.MANUAL)                                        // manual mode
            {
                UpdateManual(elapsedClockSeconds);
            }

            else if (ControlMode == TRAIN_CONTROL.EXPLORER)                                 // explorer mode
            {
                UpdateExplorer(elapsedClockSeconds);
            }

            else if (ValidRoute[0] != null && GetAIMovementState() != AITrain.AI_MOVEMENT_STATE.AI_STATIC)     // no actions required for static objects //
            {
                if (ControlMode != TRAIN_CONTROL.OUT_OF_CONTROL) movedBackward = CheckBackwardClearance();  // check clearance at rear if not out of control //
                UpdateTrainPosition();                                                          // position update         //
                UpdateTrainPositionInformation();                                               // position update         //
                int SignalObjIndex = CheckSignalPassed(0, PresentPosition[0], PreviousPosition[0]);   // check if passed signal  //
                UpdateSectionState(movedBackward);                                              // update track occupation //
                if (!(this is AITrain && (this as AITrain).MovementState == AITrain.AI_MOVEMENT_STATE.SUSPENDED)) ObtainRequiredActions(movedBackward);    // process list of actions //

                if (TrainType == TRAINTYPE.PLAYER && CheckStations) // if player train is to check own stations
                {
                    CheckStationTask();
                    CheckPlayerAttachState();                                                   // check for player attach
                }

                bool stillExist = true;
                if ((TrainType != TRAINTYPE.AI && TrainType != TRAINTYPE.AI_PLAYERHOSTING) && ControlMode != TRAIN_CONTROL.OUT_OF_CONTROL)
                {
                    stillExist = CheckRouteActions(elapsedClockSeconds);                          // check routepath (AI check at other point) //
                }

                if (stillExist)
                {
                    UpdateRouteClearanceAhead(SignalObjIndex, movedBackward, elapsedClockSeconds);  // update route clearance  //
                    if (!(TrainType == TRAINTYPE.REMOTE && MPManager.IsClient()))
                        UpdateSignalState(movedBackward);                                               // update signal state     //
                }
            }

            // calculate minimal delay (Timetable only)
            UpdateMinimalDelay();

            // check position of train wrt tunnels
            ProcessTunnels();

            // log train details

            if (DatalogTrainSpeed)
            {
                LogTrainSpeed(Simulator.ClockTime);
            }

        } // end Update

        //================================================================================================//
        /// <summary>
        /// Update train physics
        /// <\summary>

        public virtual void physicsUpdate(float elapsedClockSeconds)
        {
            //if out of track, will set it to stop
            if ((FrontTDBTraveller != null && FrontTDBTraveller.IsEnd) || (RearTDBTraveller != null && RearTDBTraveller.IsEnd))
            {
                if (FrontTDBTraveller.IsEnd && RearTDBTraveller.IsEnd)
                {//if both travellers are out, very rare occation, but have to treat it
                    RearTDBTraveller.ReverseDirection();
                    RearTDBTraveller.NextTrackNode();
                }
                else if (FrontTDBTraveller.IsEnd) RearTDBTraveller.Move(-1);//if front is out, move back
                else if (RearTDBTraveller.IsEnd) RearTDBTraveller.Move(1);//if rear is out, move forward
                foreach (var car in Cars) { car.SpeedMpS = 0; } //can set crash here by setting XNA matrix
                SignalEvent(Event._ResetWheelSlip);//reset everything to 0 power
            }

            if (this.TrainType == TRAINTYPE.REMOTE || updateMSGReceived == true) //server tolds me this train (may include mine) needs to update position
            {
                UpdateRemoteTrainPos(elapsedClockSeconds);
                return;
            }
            // Update train physics, position and movement

            PropagateBrakePressure(elapsedClockSeconds);

            bool whlslp = false;
            bool whlslpwrn = false;
            bool whlskd = false;

            TrainCar uncoupleBehindCar = null;

            float massKg = 0f;
            foreach (TrainCar car in Cars)
            {
                car.MotiveForceN = 0;
                car.Update(elapsedClockSeconds);
                car.TotalForceN = (car is MSTSLocomotive && (car as MSTSLocomotive).DynamicBrakeForceN > 0? 0 : car.MotiveForceN) + car.GravityForceN;
                massKg += car.MassKG;
                //TODO: next code line has been modified to flip trainset physics in order to get viewing direction coincident with loco direction when using rear cab.
                // To achieve the same result with other means, without flipping trainset physics, the line should be changed as follows:
                //                 if (car.Flipped)
                if (car.Flipped ^ (car.IsDriveable && car.Train.IsActualPlayerTrain && ((MSTSLocomotive)car).UsingRearCab))
                {
                    car.TotalForceN = -car.TotalForceN;
                    car.SpeedMpS = -car.SpeedMpS;
                }
                if (car.WheelSlip)
                    whlslp = true;
                if (car.WheelSlipWarning)
                    whlslpwrn = true;
                if (car.BrakeSkid)
                {
                    whlskd = true;
                    car.HUDBrakeSkid = true;
                }
                else
                {
                    car.HUDBrakeSkid = false;
                }

                if (car is MSTSDieselLocomotive || car is MSTSElectricLocomotive)
                {
                    // Test to see if locomotive is skidding for HUD presentation
                    if (car.BrakeRetardForceN > 25.0f && car.WheelSlip && car.ThrottlePercent < 0.1f)  // throttle is not good as it may not be zero? better brake? Think about more
                    {
                        whlskd = true;
                        car.HUDBrakeSkid = true;
                    }
                    else
                    {
                        car.HUDBrakeSkid = false;
                    }

                }

                if (car.CouplerExceedBreakLimit)
                    uncoupleBehindCar = car;
            }
            MassKg = massKg;

            IsWheelSlip = whlslp;
            IsWheelSlipWarninq = whlslpwrn;
            IsBrakeSkid = whlskd;

            // Coupler breaker
            if (uncoupleBehindCar != null)
            {
                if (uncoupleBehindCar.CouplerExceedBreakLimit)
                {
                    if (!numOfCouplerBreaksNoted)
                    {
                        NumOfCouplerBreaks++;
                        DbfEvalValueChanged = true;//Debrief eval

                        Trace.WriteLine(String.Format("Num of coupler breaks: {0}", NumOfCouplerBreaks));
                        numOfCouplerBreaksNoted = true;

                        if (Simulator.BreakCouplers)
                        {
                            Simulator.UncoupleBehind(uncoupleBehindCar, true);
                            uncoupleBehindCar.CouplerExceedBreakLimit = false;
                            Simulator.Confirmer.Warning(Simulator.Catalog.GetString("Coupler broken!"));
                        }
                        else
                            Simulator.Confirmer.Warning(Simulator.Catalog.GetString("Coupler overloaded!"));
                    }
                }
                else
                    numOfCouplerBreaksNoted = false;

                uncoupleBehindCar = null;
            }
            else
                numOfCouplerBreaksNoted = false;


            UpdateCarSteamHeat(elapsedClockSeconds);
            UpdateAuxTender();

            AddCouplerImpulseForces();
            ComputeCouplerForces();

            UpdateCarSpeeds(elapsedClockSeconds);
            UpdateCouplerSlack(elapsedClockSeconds);

//            Trace.TraceInformation("CouplerSlack - CarID {0} Slack1M {1} Slack2M {2}", Cars[3].CarID, Cars[3].CouplerSlackM, Cars[3].CouplerSlack2M);

            // Update wind elements for the train, ie the wind speed, and direction, as well as the angle between the train and wind
            UpdateWindComponents();

            float distanceM = LastCar.SpeedMpS * elapsedClockSeconds;
            if (float.IsNaN(distanceM)) distanceM = 0;//avoid NaN, if so will not move
            if (TrainType == TRAINTYPE.AI && LeadLocomotiveIndex == (Cars.Count - 1) && LastCar.Flipped)
                distanceM = -distanceM;
            DistanceTravelledM += distanceM;

            SpeedMpS = 0;
            foreach (TrainCar car1 in Cars)
            {
                SpeedMpS += car1.SpeedMpS;
                //TODO: next code line has been modified to flip trainset physics in order to get viewing direction coincident with loco direction when using rear cab.
                // To achieve the same result with other means, without flipping trainset physics, the line should be changed as follows:
                //                 if (car1.Flipped)
                if (car1.Flipped ^ (car1.IsDriveable && car1.Train.IsActualPlayerTrain && ((MSTSLocomotive)car1).UsingRearCab))
                    car1.SpeedMpS = -car1.SpeedMpS;
            }
#if DEBUG_SPEED_FORCES
            Trace.TraceInformation(" ========================= Train Speed #1 (Train.cs) ======================================== ");
            Trace.TraceInformation("Total Raw Speed {0} Train Speed {1}", SpeedMpS, SpeedMpS / Cars.Count);
#endif
            // This next statement looks odd - how can you find the updated speed of the train just by averaging the speeds of
            // the individual TrainCars? No problem if all the TrainCars had equal masses but, if they differ, then surely
            // you must find the total force on the train and then divide by the total mass?
            // Not to worry as comparison with those totals shows that this statement does indeed give a correct updated speed !
            //
            // The reason, I believe, is that when the train forces are balanced (e.g. constant power on a constant gradient),
            // then the calculation of forces in the couplers works out them out so that all the TrainCars share the
            // same acceleration.
            //
            // The updated speed for each TrainCar is simply calculated from the mass of the TrainCar and the force on it but
            // the force on it was previously such that all the TrainCars have the same acceleration. There is little need to
            // add them up and average them, as they only differ when the train forces are out of balance - Chris Jakeman 4-Mar-2019
            SpeedMpS /= Cars.Count;

            SlipperySpotDistanceM -= SpeedMpS * elapsedClockSeconds;
            if (ControlMode != TRAIN_CONTROL.TURNTABLE)
                CalculatePositionOfCars(elapsedClockSeconds, distanceM);

            // calculate projected speed
            if (elapsedClockSeconds < AccelerationMpSpS.SmoothPeriodS)
                AccelerationMpSpS.Update(elapsedClockSeconds, (SpeedMpS - LastSpeedMpS) / elapsedClockSeconds);
            LastSpeedMpS = SpeedMpS;
            ProjectedSpeedMpS = SpeedMpS + 60 * AccelerationMpSpS.SmoothedValue;
            ProjectedSpeedMpS = SpeedMpS > float.Epsilon ?
                Math.Max(0, ProjectedSpeedMpS) : SpeedMpS < -float.Epsilon ? Math.Min(0, ProjectedSpeedMpS) : 0;
        }

        //================================================================================================//
        /// <summary>
        /// Update Wind components for the train
        /// <\summary>

        public void UpdateWindComponents()
        {
            // Gets wind direction and speed, and determines HUD display values for the train as a whole. 
            //These will be representative of the train whilst it is on a straight track, but each wagon will vary when going around a curve.
            // Note both train and wind direction will be positive between 0 (north) and 180 (south) through east, and negative between 0 (north) and 180 (south) through west
            // Wind and train direction to be converted to an angle between 0 and 360 deg.
            if (TrainWindResistanceDependent)
            {
                // Calculate Wind speed and direction, and train direction
                // Update the value of the Wind Speed and Direction for the train
                PhysicsWindDirectionDeg = MathHelper.ToDegrees(Simulator.Weather.WindDirection);
                PhysicsWindSpeedMpS = Simulator.Weather.WindSpeed;
                float TrainSpeedMpS = Math.Abs(SpeedMpS);

                // If a westerly direction (ie -ve) convert to an angle between 0 and 360
                if (PhysicsWindDirectionDeg < 0)
                    PhysicsWindDirectionDeg += 360;

                if (PhysicsTrainLocoDirectionDeg < 0)
                    PhysicsTrainLocoDirectionDeg += 360;

                // calculate angle between train and eind direction
                if (PhysicsWindDirectionDeg > PhysicsTrainLocoDirectionDeg)
                    ResultantWindComponentDeg = PhysicsWindDirectionDeg - PhysicsTrainLocoDirectionDeg;
                else if (PhysicsTrainLocoDirectionDeg > PhysicsWindDirectionDeg)
                    ResultantWindComponentDeg = PhysicsTrainLocoDirectionDeg - PhysicsWindDirectionDeg;
                else
                    ResultantWindComponentDeg = 0.0f;

//                Trace.TraceInformation("WindDeg {0} TrainDeg {1} ResWindDeg {2}", PhysicsWindDirectionDeg, PhysicsTrainLocoDirectionDeg, ResultantWindComponentDeg);

                // Correct wind direction if it is greater then 360 deg, then correct to a value less then 360
                if (Math.Abs(ResultantWindComponentDeg) > 360)
                    ResultantWindComponentDeg = ResultantWindComponentDeg - 360.0f;

                // Wind angle should be kept between 0 and 180 the formulas do not cope with angles > 180. If angle > 180, denotes wind of "other" side of train
                if (ResultantWindComponentDeg > 180)
                    ResultantWindComponentDeg = 360 - ResultantWindComponentDeg;

                float WindAngleRad = MathHelper.ToRadians(ResultantWindComponentDeg);

                WindResultantSpeedMpS = (float)Math.Sqrt(TrainSpeedMpS * TrainSpeedMpS + PhysicsWindSpeedMpS * PhysicsWindSpeedMpS + 2.0f * TrainSpeedMpS * PhysicsWindSpeedMpS * (float)Math.Cos(WindAngleRad));

//                Trace.TraceInformation("WindResultant {0} ResWindDeg {1}", WindResultantSpeedMpS, ResultantWindComponentDeg);
            }
            else
            {
                WindResultantSpeedMpS = Math.Abs(SpeedMpS);
            }
        }


        //================================================================================================//
        /// <summary>
        /// Update Auxiliary Tenders added to train
        /// <\summary>

        public void UpdateAuxTender()
        {

            var mstsSteamLocomotive = Cars[0] as MSTSSteamLocomotive;  // Don't process if locomotive is not steam locomotive
            if (mstsSteamLocomotive != null)
            {
                AuxTenderFound = false;    // Flag to confirm that there is still an auxiliary tender in consist
                // Calculate when an auxiliary tender is coupled to train
                for (int i = 0; i < Cars.Count; i++)
                {

                    if (Cars[i].AuxWagonType == "AuxiliaryTender" && i > LeadLocomotiveIndex && IsPlayerDriven)  // If value has been entered for auxiliary tender & AuxTender car value is greater then the lead locomotive & and it is player driven
                    {
                        PrevWagonType = Cars[i - 1].AuxWagonType;
                        if (PrevWagonType == "Tender" || PrevWagonType == "Engine")  // Aux tender found in consist
                        {
                            if (Simulator.Activity != null) // If an activity check to see if fuel presets are used.
                            {
                                if (mstsSteamLocomotive.AuxTenderMoveFlag == false)  // If locomotive hasn't moved and Auxtender connected use fuel presets on aux tender
                                {
                                    MaxAuxTenderWaterMassKG = Cars[i].AuxTenderWaterMassKG;
                                    mstsSteamLocomotive.CurrentAuxTenderWaterMassKG = Cars[i].AuxTenderWaterMassKG * (Simulator.Activity.Tr_Activity.Tr_Activity_Header.FuelWater / 100.0f); // 
                                    IsAuxTenderCoupled = true;      // Flag to advise MSTSSteamLovcomotive that tender is set.
                                    AuxTenderFound = true;      // Auxililary tender found in consist.

                                }
                                else     // Otherwise assume aux tender not connected at start of activity and therefore full value of water mass available when connected.
                                {
                                    MaxAuxTenderWaterMassKG = Cars[i].AuxTenderWaterMassKG;
                                    mstsSteamLocomotive.CurrentAuxTenderWaterMassKG = Cars[i].AuxTenderWaterMassKG;
                                    IsAuxTenderCoupled = true;
                                    AuxTenderFound = true;      // Auxililary tender found in consist.
                                }
                            }
                            else  // In explore mode set aux tender to full water value
                            {
                                MaxAuxTenderWaterMassKG = Cars[i].AuxTenderWaterMassKG;
                                mstsSteamLocomotive.CurrentAuxTenderWaterMassKG = Cars[i].AuxTenderWaterMassKG;
                                IsAuxTenderCoupled = true;
                                AuxTenderFound = true;      // Auxililary tender found in consist.

                            }


                        }
                        else // Aux tender not found in consist
                        {
                            MaxAuxTenderWaterMassKG = 0.0f;
                            IsAuxTenderCoupled = false;
                        }

                    }

#if DEBUG_AUXTENDER
                    Trace.TraceInformation("=============================== DEBUG_AUXTENDER (Train.cs) ==============================================================");
                   // Trace.TraceInformation("Activity Fuel Value {0}", ActivityFuelLevel);
                    Trace.TraceInformation("CarID {0} AuxWagonType {1} LeadLocomotive {2} Max WaterMass {3} Current Water Mass {4}", i, Cars[i].AuxWagonType, LeadLocomotiveIndex, MaxAuxTenderWaterMassKG, mstsSteamLocomotive.CurrentAuxTenderWaterMassKG);
                    Trace.TraceInformation("Prev {0} Coupled {1}", PrevWagonType, IsAuxTenderCoupled);
#endif

                }

                if (AuxTenderFound == false && IsAuxTenderCoupled == true)     // If an auxiliary tender is not found in the consist, then assume that it has been uncoupled
                {
                    MaxAuxTenderWaterMassKG = 0.0f;     // Reset values
                    IsAuxTenderCoupled = false;
                }
            }

            //  Trace.TraceInformation("Tender uncouple - Tender Coupled {0} Water Mass {1}", IsAuxTenderCoupled, MaxAuxTenderWaterMassKG);

        }


        //================================================================================================//
        /// <summary>
        /// Update Steam Heating
        /// <\summary>

        public void UpdateCarSteamHeat(float elapsedClockSeconds)
        {
            var mstsLocomotive = Cars[0] as MSTSLocomotive;
            if (mstsLocomotive != null)
            { 

            if (IsTrainSteamHeatInitial) // First time method processed do this loop to set up the temprature tables
            {
                OutsideWinterTempbyLatitudeC = WorldWinterLatitudetoTemperatureC();
                OutsideAutumnTempbyLatitudeC = WorldAutumnLatitudetoTemperatureC();
                OutsideSpringTempbyLatitudeC = WorldSpringLatitudetoTemperatureC();
                OutsideSummerTempbyLatitudeC = WorldSummerLatitudetoTemperatureC();
            }

                // Check to confirm that train is player driven and has passenger cars in the consist.
                if (IsPlayerDriven && PassengerCarsNumber > 0 && mstsLocomotive.TrainFittedSteamHeat)
                {

                    // Find the latitude reading and set outside temperature
                    double latitude = 0;
                    double longitude = 0;
                    var location = this.FrontTDBTraveller;
                    new Orts.Common.WorldLatLon().ConvertWTC(location.TileX, location.TileZ, location.Location, ref latitude, ref longitude);
                    float LatitudeDeg = MathHelper.ToDegrees((float)latitude);

                    // Sets outside temperature dependent upon the season
                    if (Simulator.Season == SeasonType.Winter)
                    {
                        // Winter temps
                        TrainOutsideTempC = OutsideWinterTempbyLatitudeC[LatitudeDeg];
                    }
                    else if (Simulator.Season == SeasonType.Autumn)
                    {
                        // Autumn temps
                        TrainOutsideTempC = OutsideAutumnTempbyLatitudeC[LatitudeDeg];
                    }
                    else if (Simulator.Season == SeasonType.Spring)
                    {
                        // Sping temps
                        TrainOutsideTempC = OutsideSpringTempbyLatitudeC[LatitudeDeg];
                    }
                    else
                    {
                        // Summer temps
                        TrainOutsideTempC = OutsideSummerTempbyLatitudeC[LatitudeDeg];
                    }

                    // Reset Values to zero to recalculate values
                    TrainHeatVolumeM3 = 0.0f;
                    TrainHeatPipeAreaM2 = 0.0f;
                    TrainSteamHeatLossWpT = 0.0f;

                    // Calculate total heat loss for whole train
                    for (int i = 0; i < Cars.Count; i++)
                    {
                        TrainSteamHeatLossWpT += Cars[i].CarHeatLossWpT;
                        TrainHeatPipeAreaM2 += Cars[i].CarHeatPipeAreaM2;
                        TrainHeatVolumeM3 += Cars[i].CarHeatVolumeM3;
                    }

                    // Carriage temperature will be equal to heat input (from steam pipe) less heat losses through carriage walls, etc
                    // Calculate Heat in Train
                    TrainTotalSteamHeatW = SpecificHeatCapcityAirKJpKgK * DensityAirKgpM3 * TrainHeatVolumeM3 * (TrainInsideTempC - TrainOutsideTempC);

                    if (TrainNetSteamHeatLossWpTime < 0)
                    {
                        TrainNetSteamHeatLossWpTime = -1.0f * TrainNetSteamHeatLossWpTime; // If steam heat loss is negative, convert to a positive number
                        TrainCurrentTrainSteamHeatW -= TrainNetSteamHeatLossWpTime * elapsedClockSeconds;  // Losses per elapsed time
                    }
                    else
                    {
                        TrainCurrentTrainSteamHeatW += TrainNetSteamHeatLossWpTime * elapsedClockSeconds;  // Gains per elapsed time         
                    }

                    float MaximumHeatTempC = 37.778f;     // Allow heat to go to 100oF (37.778oC)

                    if (IsTrainSteamHeatInitial)
                    {
                        // First time this method is processed do this loop
                        TrainCurrentTrainSteamHeatW = (TrainCurrentCarriageHeatTempC - TrainOutsideTempC) / (TrainInsideTempC - TrainOutsideTempC) * TrainTotalSteamHeatW;
                        IsTrainSteamHeatInitial = false;
                    }
                    else
                    {
                        // After initialisation do this loop
                        if (TrainCurrentCarriageHeatTempC <= MaximumHeatTempC && TrainTotalSteamHeatW > 0.0)
                        {
                            if (TrainCurrentCarriageHeatTempC >= TrainOutsideTempC)
                            {
                                TrainCurrentCarriageHeatTempC = (((TrainInsideTempC - TrainOutsideTempC) * TrainCurrentTrainSteamHeatW) / TrainTotalSteamHeatW) + TrainOutsideTempC;
                            }
                            else
                            {
                                // TO BE CHECKED
                                TrainCurrentCarriageHeatTempC = TrainOutsideTempC - (((TrainInsideTempC - TrainOutsideTempC) * TrainCurrentTrainSteamHeatW) / TrainTotalSteamHeatW);
                            }
                            
                        }

        
                        TrainSteamPipeHeatConvW = (PipeHeatTransCoeffWpM2K * TrainHeatPipeAreaM2 * (C.ToK(TrainCurrentSteamHeatPipeTempC) - C.ToK(TrainCurrentCarriageHeatTempC)));
                        float PipeTempAK = (float)Math.Pow(C.ToK(TrainCurrentSteamHeatPipeTempC), 4.0f);
                        float PipeTempBK = (float)Math.Pow(C.ToK(TrainCurrentCarriageHeatTempC), 4.0f);
                        TrainSteamHeatPipeRadW = (BoltzmanConstPipeWpM2 * (PipeTempAK - PipeTempBK));
                        TrainSteamPipeHeatW = TrainSteamPipeHeatConvW + TrainSteamHeatPipeRadW;   // heat generated by pipe per degree

                        // Calculate Net steam heat loss or gain
                        TrainNetSteamHeatLossWpTime = TrainSteamPipeHeatW - TrainSteamHeatLossWpT;

                        if (CarSteamHeatOn) // Only display warning messages if steam heating is turned on
                        {

                            DisplayTrainNetSteamHeatLossWpTime = TrainNetSteamHeatLossWpTime; // Captures raw value of heat loss for display on HUD

                            // Test to see if steam heating temp has exceeded the comfortable heating value.
                            if (TrainCurrentCarriageHeatTempC > 23.8889f) // If temp above 75of (23.889oC) then alarm
                            //     if (TrainCurrentCarriageHeatTempC > TrainInsideTempC)
                            {
                                if (!IsSteamHeatExceeded)
                                {
                                    IsSteamHeatExceeded = true;
                                    // Provide warning message if temperature is too hot
                                    Simulator.Confirmer.Message(ConfirmLevel.Warning, Simulator.Catalog.GetString("Carriage temperature is too hot, the passengers are sweating."));
                                }
                            }
                            else if (TrainCurrentCarriageHeatTempC < 22.0f)
                            //           else if (TrainCurrentCarriageHeatTempC < TrainInsideTempC - 3.0f)
                            {
                                IsSteamHeatExceeded = false;        // Reset temperature warning
                            }

                            // Test to see if steam heating temp has dropped too low.

                            if (TrainCurrentCarriageHeatTempC < 18.333f) // If temp below 65of (18.33oC) then alarm
                            {
                                if (!IsSteamHeatLow)
                                {
                                    IsSteamHeatLow = true;
                                    // Provide warning message if temperature is too hot
                                    Simulator.Confirmer.Message(ConfirmLevel.Warning, Simulator.Catalog.GetString("Carriage temperature is too cold, the passengers are freezing."));
                                }
                            }
                            else if (TrainCurrentCarriageHeatTempC > 21.0f)
                            {

                                IsSteamHeatLow = false;        // Reset temperature warning
                            }
                        }
                        else
                        {
                            DisplayTrainNetSteamHeatLossWpTime = 0.0f; // Set to zero if steam heating is off
                        }
                    }


#if DEBUG_CARSTEAMHEAT

        Trace.TraceInformation("***************************************** DEBUG_CARHEAT (Train.cs) ***************************************************************");
        Trace.TraceInformation("Steam Heating Fitted {0} Player Driven {1} Passenger Cars {2}", TrainFittedSteamHeat, IsPlayerDriven, PassengerCarsNumber);       
        Trace.TraceInformation("Inside Temp {0} Outside Temp {1}", TrainInsideTempC, TrainOutsideTempC); 
        Trace.TraceInformation("Train heat loss {0} Train heat pipe area {1} Train heat volume {2}", TrainSteamHeatLossWpT, TrainHeatPipeAreaM2, TrainHeatVolumeM3);        

#endif
                }
            }
        }

        //================================================================================================//
        /// <summary>
        /// ProcessTunnels : check position of each car in train wrt tunnel
        /// <\summary>        

        public void ProcessTunnels()
        {
            // start at front of train
            int thisSectionIndex = PresentPosition[0].TCSectionIndex;
            float thisSectionOffset = PresentPosition[0].TCOffset;
            int thisSectionDirection = PresentPosition[0].TCDirection;

            for (int icar = 0; icar <= Cars.Count - 1; icar++)
            {
                var car = Cars[icar];

                float usedCarLength = car.CarLengthM;
                float processedCarLength = 0;
                bool validSections = true;

                float? FrontCarPositionInTunnel = null;
                float? FrontCarLengthOfTunnelAhead = null;
                float? RearCarLengthOfTunnelBehind = null;
                int numTunnelPaths = 0;

                while (validSections)
                {
                    TrackCircuitSection thisSection = signalRef.TrackCircuitList[thisSectionIndex];
                    bool inTunnel = false;

                    // car spans sections
                    if ((car.CarLengthM - processedCarLength) > thisSectionOffset)
                    {
                        usedCarLength = thisSectionOffset - processedCarLength;
                    }

                    // section has tunnels
                    if (thisSection.TunnelInfo != null)
                    {
                        foreach (TrackCircuitSection.tunnelInfoData[] thisTunnel in thisSection.TunnelInfo)
                        {
                            float tunnelStartOffset = thisTunnel[thisSectionDirection].TunnelStart;
                            float tunnelEndOffset = thisTunnel[thisSectionDirection].TunnelEnd;

                            if (tunnelStartOffset > 0 && tunnelStartOffset > thisSectionOffset)      // start of tunnel is in section beyond present position - cannot be in this tunnel nor any following
                            {
                                break;
                            }

                            if (tunnelEndOffset > 0 && tunnelEndOffset < (thisSectionOffset - usedCarLength)) // beyond end of tunnel, test next
                            {
                                continue;
                            }

                            if (tunnelStartOffset <= 0 || tunnelStartOffset < (thisSectionOffset - usedCarLength)) // start of tunnel is behind
                            {
                                if (tunnelEndOffset < 0) // end of tunnel is out of this section
                                {
                                    if (processedCarLength != 0)
                                    {
                                        Trace.TraceInformation("Train : " + Name + " ; found tunnel in section " + thisSectionIndex + " with End < 0 while processed length : " + processedCarLength + "\n");
                                    }
                                }

                                inTunnel = true;

                                numTunnelPaths = thisTunnel[thisSectionDirection].numTunnelPaths;

                                // get position in tunnel
                                if (tunnelStartOffset < 0)
                                {
                                    FrontCarPositionInTunnel = thisSectionOffset + thisTunnel[thisSectionDirection].TCSStartOffset;
                                    FrontCarLengthOfTunnelAhead = thisTunnel[thisSectionDirection].TotalLength - FrontCarPositionInTunnel;
                                    RearCarLengthOfTunnelBehind = thisTunnel[thisSectionDirection].TotalLength - (FrontCarLengthOfTunnelAhead + car.CarLengthM);
                                }
                                else
                                {
                                    FrontCarPositionInTunnel = thisSectionOffset - tunnelStartOffset;
                                    FrontCarLengthOfTunnelAhead = thisTunnel[thisSectionDirection].TotalLength - FrontCarPositionInTunnel - processedCarLength;
                                    RearCarLengthOfTunnelBehind = thisTunnel[thisSectionDirection].TotalLength - (FrontCarLengthOfTunnelAhead + car.CarLengthM);
                                }

                                break;  // only test one tunnel
                            }
                        }
                    }
                    // tested this section, any need to go beyond?

                    processedCarLength += usedCarLength;
                    if (inTunnel || processedCarLength >= car.CarLengthM)
                    {
                        validSections = false;  // end of while loop through sections
                        thisSectionOffset = thisSectionOffset - usedCarLength;   // position of next car in this section

                        car.CarTunnelData.FrontPositionBeyondStartOfTunnel = FrontCarPositionInTunnel.HasValue ? FrontCarPositionInTunnel : null;
                        car.CarTunnelData.LengthMOfTunnelAheadFront = FrontCarLengthOfTunnelAhead.HasValue ? FrontCarLengthOfTunnelAhead : null;
                        car.CarTunnelData.LengthMOfTunnelBehindRear = RearCarLengthOfTunnelBehind.HasValue ? RearCarLengthOfTunnelBehind : null;
                        car.CarTunnelData.numTunnelPaths = numTunnelPaths;
                    }
                    else
                    {
                        // go back one section
                        int thisSectionRouteIndex = ValidRoute[0].GetRouteIndexBackward(thisSectionIndex, PresentPosition[0].RouteListIndex);
                        if (thisSectionRouteIndex >= 0)
                        {
                            thisSectionIndex = thisSectionRouteIndex;
                            thisSection = signalRef.TrackCircuitList[thisSectionIndex];
                            thisSectionOffset = thisSection.Length;  // always at end of next section
                            thisSectionDirection = ValidRoute[0][thisSectionRouteIndex].Direction;
                        }
                        else // ran out of train
                        {
                            validSections = false;

                            car.CarTunnelData.FrontPositionBeyondStartOfTunnel = FrontCarPositionInTunnel.HasValue ? FrontCarPositionInTunnel : null;
                            car.CarTunnelData.LengthMOfTunnelAheadFront = FrontCarLengthOfTunnelAhead.HasValue ? FrontCarLengthOfTunnelAhead : null;
                            car.CarTunnelData.LengthMOfTunnelBehindRear = RearCarLengthOfTunnelBehind.HasValue ? RearCarLengthOfTunnelBehind : null;
                            car.CarTunnelData.numTunnelPaths = numTunnelPaths;
                        }
                    }
                }
            }
        }

        //================================================================================================//
        /// <summary>
        /// Train speed evaluation logging - open file
        /// <\summary>

        public void CreateLogFile()
        {
            //Time, Train Speed, Max Speed, Signal Aspect, Elevation, Direction, Control Mode, Distance Travelled, Throttle, Brake, Dyn Brake, Gear

            var stringBuild = new StringBuilder();

            if (!File.Exists(DataLogFile))
            {
                char Separator = (char)(DataLogger.Separators)Enum.Parse(typeof(DataLogger.Separators), Simulator.Settings.DataLoggerSeparator);

                if (DatalogTSContents[0] == 1)
                {
                    stringBuild.Append("TIME");
                    stringBuild.Append(Separator);
                }

                if (DatalogTSContents[1] == 1)
                {
                    stringBuild.Append("TRAINSPEED");
                    stringBuild.Append(Separator);
                }

                if (DatalogTSContents[2] == 1)
                {
                    stringBuild.Append("MAXSPEED");
                    stringBuild.Append(Separator);
                }

                if (DatalogTSContents[3] == 1)
                {
                    stringBuild.Append("SIGNALASPECT");
                    stringBuild.Append(Separator);
                }

                if (DatalogTSContents[4] == 1)
                {
                    stringBuild.Append("ELEVATION");
                    stringBuild.Append(Separator);
                }

                if (DatalogTSContents[5] == 1)
                {
                    stringBuild.Append("DIRECTION");
                    stringBuild.Append(Separator);
                }

                if (DatalogTSContents[6] == 1)
                {
                    stringBuild.Append("CONTROLMODE");
                    stringBuild.Append(Separator);
                }

                if (DatalogTSContents[7] == 1)
                {
                    stringBuild.Append("DISTANCETRAVELLED");
                    stringBuild.Append(Separator);
                }

                if (DatalogTSContents[8] == 1)
                {
                    stringBuild.Append("THROTTLEPERC");
                    stringBuild.Append(Separator);
                }

                if (DatalogTSContents[9] == 1)
                {
                    stringBuild.Append("BRAKEPRESSURE");
                    stringBuild.Append(Separator);
                }

                if (DatalogTSContents[10] == 1)
                {
                    stringBuild.Append("DYNBRAKEPERC");
                    stringBuild.Append(Separator);
                }

                if (DatalogTSContents[11] == 1)
                {
                    stringBuild.Append("GEARINDEX");
                    stringBuild.Append(Separator);
                }

                stringBuild.Append("\n");

                try
                {
                    File.AppendAllText(DataLogFile, stringBuild.ToString());
                }
                catch (Exception e)
                {
                    Trace.TraceWarning("Cannot open required logfile : " + DataLogFile + " : " + e.Message);
                    DatalogTrainSpeed = false;
                }
            }
        }

        //================================================================================================//
        /// <summary>
        /// Train speed evaluation logging
        /// <\summary>

        public void LogTrainSpeed(double clockTime)
        {
            int clockInt = Convert.ToInt32(clockTime);
            int deltaLastLog = clockInt - lastSpeedLog;

            if (deltaLastLog >= DatalogTSInterval)
            {
                lastSpeedLog = clockInt;

                // User settings flag indices :
                //Time, Train Speed, Max Speed, Signal Aspect, Elevation, Direction, Control Mode, Distance Travelled, Throttle, Brake, Dyn Brake, Gear

                var stringBuild = new StringBuilder();

                char Separator = (char)(DataLogger.Separators)Enum.Parse(typeof(DataLogger.Separators), Simulator.Settings.DataLoggerSeparator);

                if (DatalogTSContents[0] == 1)
                {
                    stringBuild.Append(FormatStrings.FormatTime(clockTime));
                    stringBuild.Append(Separator);
                }

                bool moveForward = (Math.Sign(SpeedMpS) >= 0);
                if (DatalogTSContents[1] == 1)
                {
                    stringBuild.Append(MpS.FromMpS(Math.Abs(SpeedMpS), Simulator.MilepostUnitsMetric).ToString("0000.0"));
                    stringBuild.Append(Separator);
                }

                if (DatalogTSContents[2] == 1)
                {
                    stringBuild.Append(MpS.FromMpS(AllowedMaxSpeedMpS, Simulator.MilepostUnitsMetric).ToString("0000.0"));
                    stringBuild.Append(Separator);
                }

                if (DatalogTSContents[3] == 1)
                {
                    if (moveForward)
                    {
                        if (NextSignalObject[0] == null)
                        {
                            stringBuild.Append("-");
                        }
                        else
                        {
                            MstsSignalAspect nextAspect = NextSignalObject[0].this_sig_lr(MstsSignalFunction.NORMAL);
                            stringBuild.Append(nextAspect.ToString());
                        }
                    }
                    else
                    {
                        if (NextSignalObject[1] == null)
                        {
                            stringBuild.Append("-");
                        }
                        else
                        {
                            MstsSignalAspect nextAspect = NextSignalObject[1].this_sig_lr(MstsSignalFunction.NORMAL);
                            stringBuild.Append(nextAspect.ToString());
                        }
                    }
                    stringBuild.Append(Separator);
                }

                if (DatalogTSContents[4] == 1)
                {
                    stringBuild.Append((0 - Cars[LeadLocomotiveIndex].CurrentElevationPercent).ToString("00.0"));
                    stringBuild.Append(Separator);
                }

                if (DatalogTSContents[5] == 1)
                {
                    if (moveForward)
                    {
                        stringBuild.Append("F");
                    }
                    else
                    {
                        stringBuild.Append("B");
                    }
                    stringBuild.Append(Separator);
                }

                if (DatalogTSContents[6] == 1)
                {
                    stringBuild.Append(ControlMode.ToString());
                    stringBuild.Append(Separator);
                }

                if (DatalogTSContents[7] == 1)
                {
                    stringBuild.Append(PresentPosition[0].DistanceTravelledM.ToString());
                    stringBuild.Append(Separator);
                }

                if (DatalogTSContents[8] == 1)
                {
                    stringBuild.Append(MUThrottlePercent.ToString("000"));
                    stringBuild.Append(Separator);
                }

                if (DatalogTSContents[9] == 1)
                {
                    //                    stringBuild.Append(BrakeLine1PressurePSIorInHg.ToString("000"));
                    stringBuild.Append(Cars[LeadLocomotiveIndex].BrakeSystem.GetCylPressurePSI().ToString("000"));
                    stringBuild.Append(Separator);
                }

                if (DatalogTSContents[10] == 1)
                {
                    stringBuild.Append(MUDynamicBrakePercent.ToString("000"));
                    stringBuild.Append(Separator);
                }

                if (DatalogTSContents[11] == 1)
                {
                    stringBuild.Append(MUGearboxGearIndex.ToString("0"));
                    stringBuild.Append(Separator);
                }

                stringBuild.Append("\n");
                File.AppendAllText(DataLogFile, stringBuild.ToString());
            }
        }

        //================================================================================================//
        /// <summary>
        /// Update in manual mode
        /// <\summary>

        public void UpdateManual(float elapsedClockSeconds)
        {
            UpdateTrainPosition();                                                                // position update                  //
            int SignalObjIndex = CheckSignalPassed(0, PresentPosition[0], PreviousPosition[0]);   // check if passed signal forward   //
            if (SignalObjIndex < 0)
            {
                SignalObjIndex = CheckSignalPassed(1, PresentPosition[1], PreviousPosition[1]);   // check if passed signal backward  //
            }
            if (SignalObjIndex >= 0)
            {
                var signalObject = signalRef.SignalObjects[SignalObjIndex];

                //the following is added by CSantucci, applying also to manual mode what Jtang implemented for activity mode: after passing a manually forced signal,
                // system will take back control of the signal
                if (signalObject.holdState == SignalObject.HoldState.ManualPass ||
                    signalObject.holdState == SignalObject.HoldState.ManualApproach) signalObject.holdState = SignalObject.HoldState.None;
            }
            UpdateSectionStateManual();                                                           // update track occupation          //
            UpdateManualMode(SignalObjIndex);                                                     // update route clearance           //
            // for manual, also includes signal update //
        }

        //================================================================================================//
        /// <summary>
        /// Update in explorer mode
        /// <\summary>

        public void UpdateExplorer(float elapsedClockSeconds)
        {
            UpdateTrainPosition();                                                                // position update                  //
            int SignalObjIndex = CheckSignalPassed(0, PresentPosition[0], PreviousPosition[0]);   // check if passed signal forward   //
            if (SignalObjIndex < 0)
            {
                SignalObjIndex = CheckSignalPassed(1, PresentPosition[1], PreviousPosition[1]);   // check if passed signal backward  //
            }
            if (SignalObjIndex >= 0)
            {
                var signalObject = signalRef.SignalObjects[SignalObjIndex];

                //the following is added by CSantucci, applying also to explorer mode what Jtang implemented for activity mode: after passing a manually forced signal,
                // system will take back control of the signal
                if (signalObject.holdState == SignalObject.HoldState.ManualPass ||
                    signalObject.holdState == SignalObject.HoldState.ManualApproach) signalObject.holdState = SignalObject.HoldState.None;
            }
            UpdateSectionStateExplorer();                                                         // update track occupation          //
            UpdateExplorerMode(SignalObjIndex);                                                   // update route clearance           //
            // for manual, also includes signal update //
        }

        //================================================================================================//
        /// <summary>
        /// Update in turntable mode
        /// <\summary>

        public void UpdateTurntable(float elapsedClockSeconds)
        {
 //           UpdateTrainPosition();                                                                // position update                  //
            if (LeadLocomotive != null && (LeadLocomotive.ThrottlePercent >= 1 || Math.Abs(LeadLocomotive.SpeedMpS) > 0.05 || !(LeadLocomotive.Direction == Direction.N
            || Math.Abs(MUReverserPercent) <= 1)) || ControlMode != TRAIN_CONTROL.TURNTABLE)
                // Go to emergency.
                {
                    ((MSTSLocomotive)LeadLocomotive).SetEmergency(true);
                }
        }

        //================================================================================================//
        /// <summary>
        /// Post Init : perform all actions required to start
        /// </summary>

        public virtual bool PostInit()
        {

            // if train has no valid route, build route over trainlength (from back to front)

            bool validPosition = InitialTrainPlacement();

            if (validPosition)
            {
                InitializeSignals(false);     // Get signal information - only if train has route //
                if (TrainType != TRAINTYPE.STATIC)
                    CheckDeadlock(ValidRoute[0], Number);    // Check deadlock against all other trains (not for static trains)
                if (TCRoute != null) TCRoute.SetReversalOffset(Length, Simulator.TimetableMode);

                AuxActionsContain.SetAuxAction(this);
            }


            // set train speed logging flag (valid per activity, so will be restored after save)

            if (IsActualPlayerTrain)
            {
                DatalogTrainSpeed = Simulator.Settings.DataLogTrainSpeed;
                DatalogTSInterval = Simulator.Settings.DataLogTSInterval;

                DatalogTSContents = new int[Simulator.Settings.DataLogTSContents.Length];
                Simulator.Settings.DataLogTSContents.CopyTo(DatalogTSContents, 0);

                // if logging required, derive filename and open file
                if (DatalogTrainSpeed)
                {
                    DataLogFile = Simulator.DeriveLogFile("Speed");
                    if (String.IsNullOrEmpty(DataLogFile))
                    {
                        DatalogTrainSpeed = false;
                    }
                    else
                    {
                        CreateLogFile();
                    }
                }

                // if debug, print out all passing paths

#if DEBUG_DEADLOCK
                Printout_PassingPaths();
#endif
            }

            return (validPosition);
        }

        //================================================================================================//
        /// <summary>
        /// get aspect of next signal ahead
        /// </summary>

        public MstsSignalAspect GetNextSignalAspect(int direction)
        {
            MstsSignalAspect thisAspect = MstsSignalAspect.STOP;
            if (NextSignalObject[direction] != null)
            {
                thisAspect = NextSignalObject[direction].this_sig_lr(MstsSignalFunction.NORMAL);
            }

            return thisAspect;
        }

        //================================================================================================//
        /// <summary>
        /// initialize signal array
        /// </summary>

        public void InitializeSignals(bool existingSpeedLimits)
        {
            Debug.Assert(signalRef != null, "Cannot InitializeSignals() without Simulator.Signals.");

            // to initialize, use direction 0 only
            // preset indices

            SignalObjectItems.Clear();
            IndexNextSignal = -1;
            IndexNextSpeedlimit = -1;

            //  set overall speed limits if these do not yet exist

            if (!existingSpeedLimits)
            {
                if ((TrainMaxSpeedMpS <= 0f) && (this.LeadLocomotive != null))
                    TrainMaxSpeedMpS = (this.LeadLocomotive as MSTSLocomotive).MaxSpeedMpS;
                AllowedMaxSpeedMpS = TrainMaxSpeedMpS;   // set default
                allowedMaxSpeedSignalMpS = TrainMaxSpeedMpS;   // set default
                allowedMaxTempSpeedLimitMpS = AllowedMaxSpeedMpS; // set default

                //  try to find first speed limits behind the train

                List<int> speedpostList = signalRef.ScanRoute(null, PresentPosition[1].TCSectionIndex, PresentPosition[1].TCOffset,
                                PresentPosition[1].TCDirection, false, -1, false, true, false, false, false, false, false, true, false, IsFreight);

                if (speedpostList.Count > 0)
                {
                    var thisSpeedpost = signalRef.SignalObjects[speedpostList[0]];
                    var speed_info = thisSpeedpost.this_lim_speed(MstsSignalFunction.SPEED);

                    AllowedMaxSpeedMpS = Math.Min(AllowedMaxSpeedMpS, IsFreight ? speed_info.speed_freight : speed_info.speed_pass);
                    allowedAbsoluteMaxSpeedLimitMpS =  Math.Min(allowedAbsoluteMaxSpeedLimitMpS , IsFreight ? speed_info.speed_freight : speed_info.speed_pass);
                }

                float validSpeedMpS = AllowedMaxSpeedMpS;

                //  try to find first speed limits along train - scan back to front

                bool noMoreSpeedposts = false;
                int thisSectionIndex = PresentPosition[1].TCSectionIndex;
                float thisSectionOffset = PresentPosition[1].TCOffset;
                int thisDirection = PresentPosition[1].TCDirection;
                float remLength = Length;

                while (!noMoreSpeedposts)
                {
                    speedpostList = signalRef.ScanRoute(null, thisSectionIndex, thisSectionOffset,
                            thisDirection, true, remLength, false, true, false, false, false, false, false, true, false, IsFreight);

                    if (speedpostList.Count > 0)
                    {
                        var thisSpeedpost = signalRef.SignalObjects[speedpostList[0]];
                        var speed_info = thisSpeedpost.this_lim_speed(MstsSignalFunction.SPEED);
                        float distanceFromFront = Length - thisSpeedpost.DistanceTo(RearTDBTraveller);
                        if (distanceFromFront >= 0)
                        {
                            float newSpeedMpS = IsFreight ? speed_info.speed_freight : speed_info.speed_pass;
                            if (newSpeedMpS <= validSpeedMpS)
                            {
                                validSpeedMpS = newSpeedMpS;
                                if (validSpeedMpS < AllowedMaxSpeedMpS)
                                {
                                    AllowedMaxSpeedMpS = validSpeedMpS;
                                }
                                requiredActions.UpdatePendingSpeedlimits(validSpeedMpS);  // update any older pending speed limits
                            }
                            else
                            {
                                validSpeedMpS = newSpeedMpS;
                                float reqDistance = DistanceTravelledM + Length - distanceFromFront;
                                ActivateSpeedLimit speedLimit = new ActivateSpeedLimit(reqDistance,
                                    speed_info.speed_noSpeedReductionOrIsTempSpeedReduction == 0 ? newSpeedMpS : -1, -1f,
                                    speed_info.speed_noSpeedReductionOrIsTempSpeedReduction == 0 ? -1 : newSpeedMpS);
                                requiredActions.InsertAction(speedLimit);
                                requiredActions.UpdatePendingSpeedlimits(newSpeedMpS);  // update any older pending speed limits
                            }

                            if (newSpeedMpS < allowedAbsoluteMaxSpeedLimitMpS) allowedAbsoluteMaxSpeedLimitMpS = newSpeedMpS;
                            thisSectionIndex = thisSpeedpost.TCReference;
                            thisSectionOffset = thisSpeedpost.TCOffset;
                            thisDirection = thisSpeedpost.TCDirection;
                            remLength = distanceFromFront;
                        }
                        else
                        {
                            noMoreSpeedposts = true;
                        }
                    }
                    else
                    {
                        noMoreSpeedposts = true;
                    }
                }

                allowedMaxSpeedLimitMpS = AllowedMaxSpeedMpS;   // set default
            }

            //  get first item from train (irrespective of distance)

            ObjectItemInfo.ObjectItemFindState returnState = ObjectItemInfo.ObjectItemFindState.None;
            float distanceToLastObject = 9E29f;  // set to overlarge value
            MstsSignalAspect nextAspect = MstsSignalAspect.UNKNOWN;

            ObjectItemInfo firstObject = signalRef.GetNextObject_InRoute(routedForward, ValidRoute[0],
                PresentPosition[0].RouteListIndex, PresentPosition[0].TCOffset, -1,
                ObjectItemInfo.ObjectItemType.Any);

            returnState = firstObject.ObjectState;
            if (returnState == ObjectItemInfo.ObjectItemFindState.Object)
            {
                firstObject.distance_to_train = firstObject.distance_found;
                SignalObjectItems.Add(firstObject);
                if (firstObject.ObjectDetails.isSignal)
                {
                    nextAspect = firstObject.ObjectDetails.this_sig_lr(MstsSignalFunction.NORMAL);
                    firstObject.signal_state = nextAspect;
                }
                distanceToLastObject = firstObject.distance_found;
            }

            // get next items within max distance

            float maxDistance = Math.Max(AllowedMaxSpeedMpS * maxTimeS, minCheckDistanceM);

            // look maxTimeS or minCheckDistance ahead

            ObjectItemInfo nextObject;
            ObjectItemInfo prevObject = firstObject;

            int routeListIndex = PresentPosition[0].RouteListIndex;
            float offset = PresentPosition[0].TCOffset;
            int nextIndex = routeListIndex;

            while (returnState == ObjectItemInfo.ObjectItemFindState.Object &&
                distanceToLastObject < maxDistance &&
                nextAspect != MstsSignalAspect.STOP)
            {
                int foundSection = -1;

                var thisSignal = prevObject.ObjectDetails;

                int reqTCReference = thisSignal.TCReference;
                float reqOffset = thisSignal.TCOffset + 0.0001f;   // make sure you find NEXT object ! //

                if (thisSignal.TCNextTC > 0)
                {
                    reqTCReference = thisSignal.TCNextTC;
                    reqOffset = 0.0f;
                }

                if (nextIndex < 0)
                    nextIndex = 0;
                for (int iNode = nextIndex; iNode < ValidRoute[0].Count && foundSection < 0 && reqTCReference > 0; iNode++)
                {
                    Train.TCRouteElement thisElement = ValidRoute[0][iNode];
                    if (thisElement.TCSectionIndex == reqTCReference)
                    {
                        foundSection = iNode;
                        nextIndex = iNode;
                        offset = reqOffset;
                    }
                }

                nextObject = signalRef.GetNextObject_InRoute(routedForward, ValidRoute[0],
                nextIndex, offset, -1, ObjectItemInfo.ObjectItemType.Any);

                returnState = nextObject.ObjectState;

                if (returnState == ObjectItemInfo.ObjectItemFindState.Object)
                {
                    if (nextObject.ObjectDetails.isSignal)
                    {
                        nextObject.signal_state = nextObject.ObjectDetails.this_sig_lr(MstsSignalFunction.NORMAL);
                        nextAspect = nextObject.signal_state;

                    }

                    nextObject.distance_to_object = nextObject.distance_found;
                    nextObject.distance_to_train = prevObject.distance_to_train + nextObject.distance_to_object;
                    distanceToLastObject = nextObject.distance_to_train;
                    SignalObjectItems.Add(nextObject);
                    prevObject = nextObject;
                }
            }

            //
            // get first signal and first speedlimit
            // also initiate nextSignal variable
            //

            bool signalFound = false;
            bool speedlimFound = false;

            for (int isig = 0; isig < SignalObjectItems.Count && (!signalFound || !speedlimFound); isig++)
            {
                if (!signalFound)
                {
                    ObjectItemInfo thisObject = SignalObjectItems[isig];
                    if (thisObject.ObjectType == ObjectItemInfo.ObjectItemType.Signal)
                    {
                        signalFound = true;
                        IndexNextSignal = isig;
                    }
                }

                if (!speedlimFound)
                {
                    ObjectItemInfo thisObject = SignalObjectItems[isig];
                    if (thisObject.ObjectType == ObjectItemInfo.ObjectItemType.Speedlimit)
                    {
                        speedlimFound = true;
                        IndexNextSpeedlimit = isig;
                    }
                }
            }

            //
            // If signal in list, set signal reference,
            // else try to get first signal if in signal mode
            //

            NextSignalObject[0] = null;
            if (IndexNextSignal >= 0)
            {
                NextSignalObject[0] = SignalObjectItems[IndexNextSignal].ObjectDetails;
                DistanceToSignal = SignalObjectItems[IndexNextSignal].distance_to_train;
            }
            else
            {
                ObjectItemInfo firstSignalObject = signalRef.GetNextObject_InRoute(routedForward, ValidRoute[0],
                    PresentPosition[0].RouteListIndex, PresentPosition[0].TCOffset, -1,
                    ObjectItemInfo.ObjectItemType.Signal);

                if (firstSignalObject.ObjectState == ObjectItemInfo.ObjectItemFindState.Object)
                {
                    NextSignalObject[0] = firstSignalObject.ObjectDetails;
                    firstSignalObject.distance_to_train = firstSignalObject.distance_found;
                    DistanceToSignal = firstSignalObject.distance_found;
                }
            }

            //
            // determine actual speed limits depending on overall speed and type of train
            //

            updateSpeedInfo();
        }

        //================================================================================================//
        /// <summary>
        ///  Update the distance to and aspect of next signal
        /// </summary>

        public void UpdateSignalState(int backward)
        {
            // for AUTO mode, use direction 0 only
            ObjectItemInfo.ObjectItemFindState returnState = ObjectItemInfo.ObjectItemFindState.Object;

            bool listChanged = false;
            bool signalFound = false;
            bool speedlimFound = false;

            ObjectItemInfo firstObject = null;

            //
            // get distance to first object
            //

            if (SignalObjectItems.Count > 0)
            {
                firstObject = SignalObjectItems[0];
                firstObject.distance_to_train = GetObjectDistanceToTrain(firstObject);


                //
                // check if passed object - if so, remove object
                // if object is speed, set max allowed speed as distance travelled action
                //

                while (firstObject.distance_to_train < 0.0f && SignalObjectItems.Count > 0)
                {
#if DEBUG_REPORTS
                    File.AppendAllText(@"C:\temp\printproc.txt", "Passed Signal : " + firstObject.ObjectDetails.thisRef.ToString() +
                        " with speed : " + firstObject.actual_speed.ToString() + "\n");
#endif
                    var temp1MaxSpeedMpS = IsFreight ? firstObject.speed_freight : firstObject.speed_passenger;
                    if (firstObject.ObjectDetails.isSignal)
                    {
                        allowedAbsoluteMaxSpeedSignalMpS = temp1MaxSpeedMpS == -1 ? (float)Simulator.TRK.Tr_RouteFile.SpeedLimit : temp1MaxSpeedMpS;
                    }
                    else if (firstObject.speed_reset == 0)
                    {
                        if (firstObject.speed_noSpeedReductionOrIsTempSpeedReduction == 0) allowedAbsoluteMaxSpeedLimitMpS = temp1MaxSpeedMpS == -1 ? allowedAbsoluteMaxSpeedLimitMpS : temp1MaxSpeedMpS;
                        else allowedAbsoluteMaxTempSpeedLimitMpS = temp1MaxSpeedMpS == -1 ? allowedAbsoluteMaxTempSpeedLimitMpS : temp1MaxSpeedMpS;
                    }
                    else
                    {
                        allowedAbsoluteMaxSpeedSignalMpS = allowedAbsoluteMaxSpeedLimitMpS;
                    }

                    if (firstObject.actual_speed > 0)
                    {
#if DEBUG_REPORTS
                        File.AppendAllText(@"C:\temp\printproc.txt", "Passed speedpost : " + firstObject.ObjectDetails.thisRef.ToString() +
                            " = " + firstObject.actual_speed.ToString() + "\n");

                        File.AppendAllText(@"C:\temp\printproc.txt", "Present Limits : " +
                            "Limit : " + allowedMaxSpeedLimitMpS.ToString() + " ; " +
                            "Signal : " + allowedMaxSpeedSignalMpS.ToString() + " ; " +
                            "Overall : " + AllowedMaxSpeedMpS.ToString() + "\n");
#endif
                        if (firstObject.actual_speed <= AllowedMaxSpeedMpS)
                        {
                            AllowedMaxSpeedMpS = firstObject.actual_speed;
                            float tempMaxSpeedMps = AllowedMaxSpeedMpS;
                            if (!Simulator.TimetableMode)
                            {
                                tempMaxSpeedMps = IsFreight ? firstObject.speed_freight : firstObject.speed_passenger;
                                if (tempMaxSpeedMps == -1f)
                                    tempMaxSpeedMps = AllowedMaxSpeedMpS;
                            }


                            if (firstObject.ObjectDetails.isSignal)
                            {
                                allowedMaxSpeedSignalMpS = tempMaxSpeedMps;
                            }
                            else if (firstObject.speed_noSpeedReductionOrIsTempSpeedReduction == 0)
                            {
                                allowedMaxSpeedLimitMpS = tempMaxSpeedMps;
                            }
                            else
                            {
                                allowedMaxTempSpeedLimitMpS = tempMaxSpeedMps;
                            }
                            requiredActions.UpdatePendingSpeedlimits(AllowedMaxSpeedMpS);  // update any older pending speed limits
                        }
                        else
                        {
                            ActivateSpeedLimit speedLimit;
                            float reqDistance = DistanceTravelledM + Length;
                            if (firstObject.ObjectDetails.isSignal)
                            {
                                speedLimit = new ActivateSpeedLimit(reqDistance, -1f, firstObject.actual_speed);
                            }
                            else if (Simulator.TimetableMode || firstObject.speed_reset == 0)
                            {
                                speedLimit = new ActivateSpeedLimit(reqDistance,
                                    firstObject.speed_noSpeedReductionOrIsTempSpeedReduction == 0 ? firstObject.actual_speed : -1, -1f,
                                    firstObject.speed_noSpeedReductionOrIsTempSpeedReduction == 0 ? -1 : firstObject.actual_speed);
                            }
                            else
                            {
                                speedLimit = new ActivateSpeedLimit(reqDistance, firstObject.actual_speed, firstObject.actual_speed);
                            }
                            
                            requiredActions.InsertAction(speedLimit);
                            requiredActions.UpdatePendingSpeedlimits(firstObject.actual_speed);  // update any older pending speed limits
                        }
                    }
                    else if (!Simulator.TimetableMode)
                    {
                        var tempMaxSpeedMps = IsFreight ? firstObject.speed_freight : firstObject.speed_passenger;
                        if (tempMaxSpeedMps >= 0)
                        {
                            if (firstObject.ObjectDetails.isSignal)
                            {
                                allowedMaxSpeedSignalMpS = tempMaxSpeedMps;
                            }
                            else
                            {
                                if (firstObject.speed_noSpeedReductionOrIsTempSpeedReduction == 0) allowedMaxSpeedLimitMpS = tempMaxSpeedMps;
                                else allowedMaxTempSpeedLimitMpS = tempMaxSpeedMps;
                            }
                        }
                        else if (firstObject.ObjectDetails.isSignal)
                        {
                            allowedMaxSpeedSignalMpS = allowedAbsoluteMaxSpeedSignalMpS;
                        }
                    }

                    if (NextSignalObject[0] != null && firstObject.ObjectDetails == NextSignalObject[0])
                    {
                        NextSignalObject[0] = null;
                    }

                    SignalObjectItems.RemoveAt(0);
                    listChanged = true;

                    if (SignalObjectItems.Count > 0)
                    {
                        firstObject = SignalObjectItems[0];
                        firstObject.distance_to_train = GetObjectDistanceToTrain(firstObject);
                    }
                }

                //
                // if moving backward, check signals have been passed
                //

                if (backward > backwardThreshold)
                {

                    int newSignalIndex = -1;
                    bool noMoreNewSignals = false;

                    int thisIndex = PresentPosition[0].RouteListIndex;
                    float offset = PresentPosition[0].TCOffset;

                    while (!noMoreNewSignals)
                    {
                        ObjectItemInfo newObjectItem = signalRef.GetNextObject_InRoute(routedForward, ValidRoute[0],
                           thisIndex, offset, -1, ObjectItemInfo.ObjectItemType.Signal);

                        returnState = newObjectItem.ObjectState;
                        if (returnState == ObjectItemInfo.ObjectItemFindState.Object)
                        {
                            newSignalIndex = newObjectItem.ObjectDetails.thisRef;

                            noMoreNewSignals = (NextSignalObject[0] == null || (NextSignalObject[0] != null && newSignalIndex == NextSignalObject[0].thisRef));

                            if (!noMoreNewSignals)
                            {
                                if (SignalObjectItems.Count > 0)  // reset distance to train to distance to object //
                                {
                                    firstObject = SignalObjectItems[0];
                                    firstObject.distance_to_object =
                                        firstObject.distance_to_train - newObjectItem.distance_to_train;
                                }

                                SignalObjectItems.Insert(0, newObjectItem);
                                listChanged = true;

                                int foundIndex = ValidRoute[0].GetRouteIndex(newObjectItem.ObjectDetails.TCNextTC, thisIndex);

                                if (foundIndex > 0)
                                {
                                    thisIndex = foundIndex;
                                    offset = 0.0f;
                                }
                            }
                        }
                        else
                        {
                            noMoreNewSignals = true;
                        }
                    }
                }
            }

            //
            // if no objects left on list, find first object whatever the distance
            //

            if (SignalObjectItems.Count <= 0)
            {
                firstObject = signalRef.GetNextObject_InRoute(routedForward, ValidRoute[0],
                      PresentPosition[0].RouteListIndex, PresentPosition[0].TCOffset, -1,
                      ObjectItemInfo.ObjectItemType.Any);

                returnState = firstObject.ObjectState;
                if (returnState == ObjectItemInfo.ObjectItemFindState.Object)
                {
                    firstObject.distance_to_train = firstObject.distance_found;
                    SignalObjectItems.Add(firstObject);
                }
            }

            // reset next signal object if none found

            if (SignalObjectItems.Count <= 0 || (SignalObjectItems.Count == 1 && SignalObjectItems[0].ObjectType == ObjectItemInfo.ObjectItemType.Speedlimit))
            {
                NextSignalObject[0] = null;
                DistanceToSignal = null;
                listChanged = true;
            }

            //
            // process further if any object available
            //

            if (SignalObjectItems.Count > 0)
            {

                //
                // Update state and speed of first object if signal
                //

                if (firstObject.ObjectDetails.isSignal)
                {
                    firstObject.signal_state = firstObject.ObjectDetails.this_sig_lr(MstsSignalFunction.NORMAL);
                    ObjectSpeedInfo thisSpeed = firstObject.ObjectDetails.this_sig_speed(MstsSignalFunction.NORMAL);
                    firstObject.speed_passenger = thisSpeed == null ? -1 : thisSpeed.speed_pass;
                    firstObject.speed_freight = thisSpeed == null ? -1 : thisSpeed.speed_freight;
                    firstObject.speed_flag = thisSpeed == null ? 0 : thisSpeed.speed_flag;
                    firstObject.speed_reset = thisSpeed == null ? 0 : thisSpeed.speed_reset;
                }
                else if (firstObject.ObjectDetails.SignalHeads != null)  // check if object is SPEED info signal
                {
                    if (firstObject.ObjectDetails.SignalHeads[0].sigFunction == MstsSignalFunction.SPEED)
                    {
                        ObjectSpeedInfo thisSpeed = firstObject.ObjectDetails.this_sig_speed(MstsSignalFunction.SPEED);
                        firstObject.speed_passenger = thisSpeed == null ? -1 : thisSpeed.speed_pass;
                        firstObject.speed_freight = thisSpeed == null ? -1 : thisSpeed.speed_freight;
                        firstObject.speed_flag = thisSpeed == null ? 0 : thisSpeed.speed_flag;
                        firstObject.speed_reset = thisSpeed == null ? 0 : thisSpeed.speed_reset;
                        firstObject.speed_noSpeedReductionOrIsTempSpeedReduction = thisSpeed == null ? 0 : thisSpeed.speed_noSpeedReductionOrIsTempSpeedReduction;
                    }
                }

                //
                // Update all objects in list (except first)
                //

                float lastDistance = firstObject.distance_to_train;

                ObjectItemInfo prevObject = firstObject;

                for (int isig = 1; isig < SignalObjectItems.Count && !signalFound; isig++)
                {
                    ObjectItemInfo nextObject = SignalObjectItems[isig];
                    nextObject.distance_to_train = prevObject.distance_to_train + nextObject.distance_to_object;
                    lastDistance = nextObject.distance_to_train;

                    if (nextObject.ObjectDetails.isSignal)
                    {
                        nextObject.signal_state = nextObject.ObjectDetails.this_sig_lr(MstsSignalFunction.NORMAL);
                        if (nextObject.ObjectDetails.enabledTrain != null && nextObject.ObjectDetails.enabledTrain.Train != this)
                            nextObject.signal_state = MstsSignalAspect.STOP; // state not valid if not enabled for this train
                        ObjectSpeedInfo thisSpeed = nextObject.ObjectDetails.this_sig_speed(MstsSignalFunction.NORMAL);
                        nextObject.speed_passenger = thisSpeed == null || nextObject.signal_state == MstsSignalAspect.STOP ? -1 : thisSpeed.speed_pass;
                        nextObject.speed_freight = thisSpeed == null || nextObject.signal_state == MstsSignalAspect.STOP ? -1 : thisSpeed.speed_freight;
                        nextObject.speed_flag = thisSpeed == null || nextObject.signal_state == MstsSignalAspect.STOP ? 0 : thisSpeed.speed_flag;
                        nextObject.speed_reset = thisSpeed == null || nextObject.signal_state == MstsSignalAspect.STOP ? 0 : thisSpeed.speed_reset;
                    }
                    else if (nextObject.ObjectDetails.SignalHeads != null)  // check if object is SPEED info signal
                    {
                        if (nextObject.ObjectDetails.SignalHeads[0].sigFunction == MstsSignalFunction.SPEED)
                        {
                            ObjectSpeedInfo thisSpeed = nextObject.ObjectDetails.this_sig_speed(MstsSignalFunction.SPEED);
                            nextObject.speed_passenger = thisSpeed == null ? -1 : thisSpeed.speed_pass;
                            nextObject.speed_freight = thisSpeed == null ? -1 : thisSpeed.speed_freight;
                            nextObject.speed_flag = thisSpeed == null ? 0 : thisSpeed.speed_flag;
                            nextObject.speed_reset = thisSpeed == null ? 0 : thisSpeed.speed_reset;
                            nextObject.speed_noSpeedReductionOrIsTempSpeedReduction = thisSpeed == null ? 0 : thisSpeed.speed_noSpeedReductionOrIsTempSpeedReduction;
                        }
                    }


                    prevObject = nextObject;
                }

                //
                // check if last signal aspect is STOP, and if last signal is enabled for this train
                // If so, no check on list is required
                //

                MstsSignalAspect nextAspect = MstsSignalAspect.UNKNOWN;

                for (int isig = SignalObjectItems.Count - 1; isig >= 0 && !signalFound; isig--)
                {
                    ObjectItemInfo nextObject = SignalObjectItems[isig];
                    if (nextObject.ObjectType == ObjectItemInfo.ObjectItemType.Signal)
                    {
                        signalFound = true;
                        nextAspect = nextObject.signal_state;
                    }
                }

                //
                // read next items if last item within max distance
                //

                float maxDistance = Math.Max(AllowedMaxSpeedMpS * maxTimeS, minCheckDistanceM);

                int routeListIndex = PresentPosition[0].RouteListIndex;
                int lastIndex = routeListIndex;
                float offset = PresentPosition[0].TCOffset;

                prevObject = SignalObjectItems[SignalObjectItems.Count - 1];  // last object

                while (lastDistance < maxDistance &&
                          returnState == ObjectItemInfo.ObjectItemFindState.Object &&
                          nextAspect != MstsSignalAspect.STOP)
                {

                    var prevSignal = prevObject.ObjectDetails;
                    int reqTCReference = prevSignal.TCReference;
                    float reqOffset = prevSignal.TCOffset + 0.0001f;   // make sure you find NEXT object ! //

                    if (prevSignal.TCNextTC > 0 && ValidRoute[0].GetRouteIndex(prevSignal.TCNextTC, lastIndex) > 0)
                    {
                        reqTCReference = prevSignal.TCNextTC;
                        reqOffset = 0.0f;
                    }

                    int foundSection = ValidRoute[0].GetRouteIndex(reqTCReference, lastIndex);
                    if (foundSection >= 0)
                    {
                        lastIndex = foundSection;
                        offset = reqOffset;
                    }

                    ObjectItemInfo nextObject = signalRef.GetNextObject_InRoute(routedForward, ValidRoute[0],
                         lastIndex, offset, -1, ObjectItemInfo.ObjectItemType.Any);

                    returnState = nextObject.ObjectState;

                    if (returnState == ObjectItemInfo.ObjectItemFindState.Object)
                    {
                        nextObject.distance_to_object = nextObject.distance_found;
                        nextObject.distance_to_train = prevObject.distance_to_train + nextObject.distance_to_object;

                        lastDistance = nextObject.distance_to_train;
                        SignalObjectItems.Add(nextObject);

                        if (nextObject.ObjectDetails.isSignal)
                        {
                            nextObject.signal_state = nextObject.ObjectDetails.this_sig_lr(MstsSignalFunction.NORMAL);
                            nextAspect = nextObject.signal_state;
                            ObjectSpeedInfo thisSpeed = nextObject.ObjectDetails.this_sig_speed(MstsSignalFunction.NORMAL);
                            nextObject.speed_passenger = thisSpeed == null ? -1 : thisSpeed.speed_pass;
                            nextObject.speed_freight = thisSpeed == null ? -1 : thisSpeed.speed_freight;
                            nextObject.speed_flag = thisSpeed == null ? 0 : thisSpeed.speed_flag;
                            nextObject.speed_reset = thisSpeed == null ? 0 : thisSpeed.speed_reset;
                        }
                        else if (nextObject.ObjectDetails.SignalHeads != null)  // check if object is SPEED info signal
                        {
                            if (nextObject.ObjectDetails.SignalHeads[0].sigFunction == MstsSignalFunction.SPEED)
                            {
                                ObjectSpeedInfo thisSpeed = nextObject.ObjectDetails.this_sig_speed(MstsSignalFunction.SPEED);
                                nextObject.speed_passenger = thisSpeed == null ? -1 : thisSpeed.speed_pass;
                                nextObject.speed_freight = thisSpeed == null ? -1 : thisSpeed.speed_freight;
                                nextObject.speed_flag = thisSpeed == null ? 0 : thisSpeed.speed_flag;
                                nextObject.speed_reset = thisSpeed == null ? 0 : thisSpeed.speed_reset;
                                nextObject.speed_noSpeedReductionOrIsTempSpeedReduction = thisSpeed == null ? 0 : thisSpeed.speed_noSpeedReductionOrIsTempSpeedReduction;
                            }
                        }

                        prevObject = nextObject;
                        listChanged = true;
                    }
                }

                //
                // check if IndexNextSignal still valid, if not, force list changed
                //

                if (IndexNextSignal >= SignalObjectItems.Count)
                {
                    if (CheckTrain)
                        File.AppendAllText(@"C:\temp\checktrain.txt", "Error in UpdateSignalState: IndexNextSignal out of range : " + IndexNextSignal +
                                             " (max value : " + SignalObjectItems.Count + ") \n");
                    listChanged = true;
                }
            }


            //
            // if list is changed, get new indices to first signal and speedpost
            //

            if (listChanged)
            {
                signalFound = false;
                speedlimFound = false;

                IndexNextSignal = -1;
                IndexNextSpeedlimit = -1;
                NextSignalObject[0] = null;

                for (int isig = 0; isig < SignalObjectItems.Count && (!signalFound || !speedlimFound); isig++)
                {
                    ObjectItemInfo nextObject = SignalObjectItems[isig];
                    if (!signalFound && nextObject.ObjectType == ObjectItemInfo.ObjectItemType.Signal)
                    {
                        signalFound = true;
                        IndexNextSignal = isig;
                    }
                    else if (!speedlimFound && nextObject.ObjectType == ObjectItemInfo.ObjectItemType.Speedlimit)
                    {
                        speedlimFound = true;
                        IndexNextSpeedlimit = isig;
                    }
                }
            }

            //
            // check if any signal in list, if not get direct from train
            // get state and details
            //

            if (IndexNextSignal < 0)
            {
                ObjectItemInfo firstSignalObject = signalRef.GetNextObject_InRoute(routedForward, ValidRoute[0],
                        PresentPosition[0].RouteListIndex, PresentPosition[0].TCOffset, -1,
                        ObjectItemInfo.ObjectItemType.Signal);

                if (firstSignalObject.ObjectState == ObjectItemInfo.ObjectItemFindState.Object)
                {
                    NextSignalObject[0] = firstSignalObject.ObjectDetails;
                    firstSignalObject.distance_to_train = firstSignalObject.distance_found;
                }
            }
            else
            {
                NextSignalObject[0] = SignalObjectItems[IndexNextSignal].ObjectDetails;
            }

            //
            // update distance of signal if out of list
            //
            if (IndexNextSignal >= 0)
            {
                DistanceToSignal = SignalObjectItems[IndexNextSignal].distance_to_train;
            }
            else if (NextSignalObject[0] != null)
            {
                DistanceToSignal = NextSignalObject[0].DistanceTo(FrontTDBTraveller);
            }
            else if (ControlMode != TRAIN_CONTROL.AUTO_NODE)
            {
                bool validModeSwitch = true;

                if (this is AITrain)
                {
                    AITrain aiTrain = this as AITrain;

                    // do not switch to node control if train is set for auxiliary action
                    if (aiTrain.nextActionInfo != null && aiTrain.nextActionInfo.NextAction == AIActionItem.AI_ACTION_TYPE.AUX_ACTION)
                    {
                        validModeSwitch = false;
                    }
                }

                if (validModeSwitch)
                { 
                    SwitchToNodeControl(LastReservedSection[0]);
                }
            }

            //
            // determine actual speed limits depending on overall speed and type of train
            //

            updateSpeedInfo();

        }

        //================================================================================================//
        /// <summary>
        /// set actual speed limit for all objects depending on state and type of train
        /// </summary>

        public void updateSpeedInfo()
        {
            float validSpeedMpS = AllowedMaxSpeedMpS;
            float validSpeedSignalMpS = allowedMaxSpeedSignalMpS;
            float validSpeedLimitMpS = allowedMaxSpeedLimitMpS;
            float validTempSpeedLimitMpS = allowedMaxTempSpeedLimitMpS;

            // update valid speed with pending actions

            foreach (var thisAction in requiredActions)
            {
                if (thisAction is ActivateSpeedLimit)
                {
                    ActivateSpeedLimit thisLimit = (thisAction as ActivateSpeedLimit);

                    if (thisLimit.MaxSpeedMpSLimit > validSpeedLimitMpS)
                    {
                        validSpeedLimitMpS = thisLimit.MaxSpeedMpSLimit;
                    }

                    if (thisLimit.MaxSpeedMpSSignal > validSpeedSignalMpS)
                    {
                        validSpeedSignalMpS = thisLimit.MaxSpeedMpSSignal;
                    }
                    if (thisLimit.MaxTempSpeedMpSLimit > validTempSpeedLimitMpS)
                    {
                        validTempSpeedLimitMpS = thisLimit.MaxTempSpeedMpSLimit;
                    }
                }
            }

            // loop through objects

            foreach (ObjectItemInfo thisObject in SignalObjectItems)
            {
                //
                // select speed on type of train 
                //

                float actualSpeedMpS = IsFreight ? thisObject.speed_freight : thisObject.speed_passenger;

                if (thisObject.ObjectDetails.isSignal)
                {
                    if (actualSpeedMpS > 0 && (thisObject.speed_flag == 0 || !Simulator.TimetableMode))
                    {
                        validSpeedSignalMpS = actualSpeedMpS;
                        if (validSpeedSignalMpS > Math.Min(validSpeedLimitMpS, validTempSpeedLimitMpS))
                        {
                            if (validSpeedMpS < Math.Min(validSpeedLimitMpS, validTempSpeedLimitMpS))
                            {
                                actualSpeedMpS = Math.Min(validSpeedLimitMpS, validTempSpeedLimitMpS);
                            }
                            else
                            {
                                actualSpeedMpS = -1;
                            }
#if DEBUG_REPORTS
                            File.AppendAllText(@"C:\temp\printproc.txt", "Speed reset : Signal : " + thisObject.ObjectDetails.thisRef.ToString() +
                                " : " + validSpeedSignalMpS.ToString() + " ; Limit : " + validSpeedLimitMpS.ToString() + "\n");
#endif
                        }
                    }
                    else
                    {
                        validSpeedSignalMpS = TrainMaxSpeedMpS;
                        float newSpeedMpS = Math.Min(validSpeedSignalMpS, Math.Min(validSpeedLimitMpS, validTempSpeedLimitMpS));

                        if (newSpeedMpS != validSpeedMpS)
                        {
                            actualSpeedMpS = newSpeedMpS;
                        }
                        else
                        {
                            actualSpeedMpS = -1;
                        }
                    }
                    thisObject.actual_speed = actualSpeedMpS;
                    if (actualSpeedMpS > 0)
                    {
                        validSpeedMpS = actualSpeedMpS;
                    }
                }
                else if (Simulator.TimetableMode)
                {
                    {
                        if (actualSpeedMpS > 998f)
                        {
                            actualSpeedMpS = TrainMaxSpeedMpS;
                        }

                        if (actualSpeedMpS > 0)
                        {
                            validSpeedMpS = actualSpeedMpS;
                            validSpeedLimitMpS = actualSpeedMpS;
                        }
                        else if (actualSpeedMpS < 0 && thisObject.speed_reset == 1)
                        {
                            validSpeedMpS = validSpeedLimitMpS;
                            actualSpeedMpS = validSpeedLimitMpS;
                        }

                        thisObject.actual_speed = Math.Min(actualSpeedMpS, TrainMaxSpeedMpS);
                    }
                }

                else  // Enhanced Compatibility on & SpeedLimit
                {
                    if (actualSpeedMpS > 998f)
                    {
                        actualSpeedMpS = (float)Simulator.TRK.Tr_RouteFile.SpeedLimit;
                    }

                    if (actualSpeedMpS > 0)
                    {
                        var tempValidSpeedSignalMpS = validSpeedSignalMpS == -1 ? 999 : validSpeedSignalMpS;
                        if (thisObject.speed_noSpeedReductionOrIsTempSpeedReduction == 0)
                        {
                            validSpeedLimitMpS = actualSpeedMpS;
                            if (actualSpeedMpS > Math.Min(tempValidSpeedSignalMpS, validTempSpeedLimitMpS))
                            {
                                if (validSpeedMpS < Math.Min(tempValidSpeedSignalMpS, validTempSpeedLimitMpS))
                                {
                                    actualSpeedMpS = Math.Min(tempValidSpeedSignalMpS, validTempSpeedLimitMpS);
                                }
                                else
                                {
                                    actualSpeedMpS = -1;
                                }
                            }
                        }
                        else
                        {
                            validTempSpeedLimitMpS = actualSpeedMpS;
                            if (actualSpeedMpS > Math.Min(tempValidSpeedSignalMpS, validSpeedLimitMpS))
                            {
                                if (validSpeedMpS < Math.Min(tempValidSpeedSignalMpS, validSpeedLimitMpS))
                                {
                                    actualSpeedMpS = Math.Min(tempValidSpeedSignalMpS, validSpeedLimitMpS);
                                }
                                else
                                {
                                    actualSpeedMpS = -1;
                                }
                            }
                        }
                    }
                    else if (actualSpeedMpS < 0 && thisObject.speed_reset == 0)
                    {
                        float newSpeedMpS1 = Math.Min(validSpeedSignalMpS, Math.Min(validSpeedLimitMpS, validTempSpeedLimitMpS));

                        if (newSpeedMpS1 != validSpeedMpS)
                        {
                            actualSpeedMpS = newSpeedMpS1;
                        }
                        else
                        {
                            actualSpeedMpS = -1;
                        }
                    }
                    else if (thisObject.speed_reset == 1)
                    {
                        actualSpeedMpS = validSpeedLimitMpS;
                    }

                    thisObject.actual_speed = actualSpeedMpS;
                    if (actualSpeedMpS > 0)
                    {
                        validSpeedMpS = actualSpeedMpS;
                    }
                }
            }
        }

        //================================================================================================//
        /// <summary>
        /// Initialize brakes
        /// <\summary>

        public virtual void InitializeBrakes()
        {
            if (Math.Abs(SpeedMpS) > 0.1)
            {
                if (Simulator.Confirmer != null) // As Confirmer may not be created until after a restore.
                    Simulator.Confirmer.Warning(CabControl.InitializeBrakes, CabSetting.Warn1);
                return;
            }
            UnconditionalInitializeBrakes();
            return;
        }

        /// <summary>
        /// Initializes brakes also if Speed != 0; directly used by keyboard command
        /// <\summary>
        public void UnconditionalInitializeBrakes()
        {
            if (Simulator.Confirmer != null && IsActualPlayerTrain) // As Confirmer may not be created until after a restore.
                Simulator.Confirmer.Confirm(CabControl.InitializeBrakes, CabSetting.Off);

            float maxPressurePSI = 90;
            float fullServPressurePSI = 64;
            if (FirstCar != null && FirstCar.BrakeSystem is VacuumSinglePipe)
            {
                maxPressurePSI = 21;
                fullServPressurePSI = 16;
            }

            if (LeadLocomotiveIndex >= 0)
            {
                MSTSLocomotive lead = (MSTSLocomotive)Cars[LeadLocomotiveIndex];
                if (lead.TrainBrakeController != null)
                {
                    lead.TrainBrakeController.UpdatePressure(ref EqualReservoirPressurePSIorInHg, 1000, ref BrakeLine4);
                    maxPressurePSI = lead.TrainBrakeController.MaxPressurePSI;
                    fullServPressurePSI = lead.BrakeSystem is VacuumSinglePipe ? 16 : maxPressurePSI - lead.TrainBrakeController.FullServReductionPSI;
                    EqualReservoirPressurePSIorInHg =
                            MathHelper.Max(EqualReservoirPressurePSIorInHg, fullServPressurePSI);
                }
                if (lead.EngineBrakeController != null)
                    lead.EngineBrakeController.UpdateEngineBrakePressure(ref BrakeLine3PressurePSI, 1000);
                if (lead.DynamicBrakeController != null)
                {
                    MUDynamicBrakePercent = lead.DynamicBrakeController.Update(1000) * 100;
                    if (MUDynamicBrakePercent == 0)
                        MUDynamicBrakePercent = -1;
                }
                BrakeLine2PressurePSI = maxPressurePSI;
                ConnectBrakeHoses();
            }
            else
            {
                EqualReservoirPressurePSIorInHg = BrakeLine2PressurePSI = BrakeLine3PressurePSI = 0;
                // Initialize static consists airless for allowing proper shunting operations,
                // but set AI trains pumped up with air.
                if (TrainType == TRAINTYPE.STATIC)
                    maxPressurePSI = 0;
                BrakeLine4 = -1;
            }
            foreach (TrainCar car in Cars)
                car.BrakeSystem.Initialize(LeadLocomotiveIndex < 0, maxPressurePSI, fullServPressurePSI, false);
        }

        //================================================================================================//
        /// <summary>
        /// Set handbrakes
        /// <\summary>

        public void SetHandbrakePercent(float percent)
        {
            if (SpeedMpS < -.1 || SpeedMpS > .1)
                return;
            foreach (TrainCar car in Cars)
                car.BrakeSystem.SetHandbrakePercent(percent);
        }

        //================================================================================================//
        /// <summary>
        /// Connect brakes
        /// <\summary>

        public void ConnectBrakeHoses()
        {
            for (var i = 0; i < Cars.Count; i++)
            {
                Cars[i].BrakeSystem.FrontBrakeHoseConnected = i > 0;
                Cars[i].BrakeSystem.AngleCockAOpen = i > 0;
                Cars[i].BrakeSystem.AngleCockBOpen = i < Cars.Count - 1;
                Cars[i].BrakeSystem.BleedOffValveOpen = false;
            }
        }

        //================================================================================================//
        /// <summary>
        /// Disconnect brakes
        /// <\summary>

        public void DisconnectBrakes()
        {
            if (SpeedMpS < -.1 || SpeedMpS > .1)
                return;
            int first = -1;
            int last = -1;
            FindLeadLocomotives(ref first, ref last);
            for (int i = 0; i < Cars.Count; i++)
            {
                Cars[i].BrakeSystem.FrontBrakeHoseConnected = first < i && i <= last;
                Cars[i].BrakeSystem.AngleCockAOpen = i != first;
                Cars[i].BrakeSystem.AngleCockBOpen = i != last;
            }
        }

        //================================================================================================//
        /// <summary>
        /// Set retainers
        /// <\summary>

        public void SetRetainers(bool increase)
        {
            if (SpeedMpS < -.1 || SpeedMpS > .1)
                return;
            if (!increase)
            {
                RetainerSetting = RetainerSetting.Exhaust;
                RetainerPercent = 100;
            }
            else if (RetainerPercent < 100)
                RetainerPercent *= 2;
            else if (RetainerSetting != RetainerSetting.SlowDirect)
            {
                RetainerPercent = 25;
                switch (RetainerSetting)
                {
                    case RetainerSetting.Exhaust:
                        RetainerSetting = RetainerSetting.LowPressure;
                        break;
                    case RetainerSetting.LowPressure:
                        RetainerSetting = RetainerSetting.HighPressure;
                        break;
                    case RetainerSetting.HighPressure:
                        RetainerSetting = RetainerSetting.SlowDirect;
                        break;
                }
            }
            int first = -1;
            int last = -1;
            FindLeadLocomotives(ref first, ref last);
            int step = 100 / RetainerPercent;
            for (int i = 0; i < Cars.Count; i++)
            {
                int j = Cars.Count - 1 - i;
                if (j <= last)
                    break;
                Cars[j].BrakeSystem.SetRetainer(i % step == 0 ? RetainerSetting : RetainerSetting.Exhaust);
            }
        }

        //================================================================================================//
        /// <summary>
        /// Find lead locomotive
        /// <\summary>

        // FindLeadLocomotives stores the index of a single locomotive, or alternatively multiple locomotives, such as 
        // in the case of MU'd diesel units, the "first" and "last" values enclose the group of locomotives where the 
        // lead locomotive (the player driven one) resides. Within this group both the main reservoir pressure and the 
        // engine brake pipe pressure will be propagated. It only identifies multiple units when coupled directly together,
        // for example a double headed steam locomotive will most often have a tender separating the two locomotives, 
        // so the second locomotive will not be identified, nor will a locomotive added at the rear of the train. 

        public void FindLeadLocomotives(ref int first, ref int last)
        {
            first = last = -1;
            if (LeadLocomotiveIndex >= 0)
            {
                for (int i = LeadLocomotiveIndex; i < Cars.Count && Cars[i].IsDriveable; i++)
                    last = i;
                for (int i = LeadLocomotiveIndex; i >= 0 && Cars[i].IsDriveable; i--)
                    first = i;
            }

            // If first (lead) locomotive is a steam locomotive check to see if the engine brake needs to be extended to cover the tender

            if (first != -1) // if lead locomotive is set at initialised value, then don't attempt to process engine brake extension
            {

                if (Cars[first] is MSTSSteamLocomotive)
                {

                    // If double headed tank steam locomotive (no tender is attached) then only apply engine brake to first locomotive for consistency
                    if (last != first && Cars[first] is MSTSSteamLocomotive && Cars[last] is MSTSSteamLocomotive)
                    {
                        last = first; // Reduce locomotive lead values to apply engine brakes only to lead locomotive, and not 2nd locomotive.
                    }
                    else // if last = first, ie only a single locomotive (can be two locomotives separated by a tender as 2nd locomotive is not counted in the first / last values.
                    {
                        if (last < Cars.Count - 1)  // Check that there are cars after the locomotive, if not skip extending brake to tender
                        {
                            if (last == first && Cars[first] is MSTSSteamLocomotive && Cars[first + 1].WagonType == TrainCar.WagonTypes.Tender)
                            {
                                last += 1;      // If a "standard" single steam locomotive with a tender then for the purposes of braking increment last above first by one
                            }
                        }
                    }
                }
            }
        }

        public TrainCar FindLeadLocomotive()
        {
            int first = -1;
            int last = -1;
            FindLeadLocomotives(ref first, ref last);
            if (first != -1 && first < LeadLocomotiveIndex)
            {
                return Cars[first];
            }
            else if (last != -1 && last > LeadLocomotiveIndex)
            {
                return Cars[last];
            }
            for (int idx = 0; idx < Cars.Count(); idx++)
            {
                if (Cars[idx].IsDriveable)
                    return Cars[idx];
            }
            return null;
        }

        //================================================================================================//
        /// <summary>
        /// Propagate brake pressure
        /// <\summary>

        public void PropagateBrakePressure(float elapsedClockSeconds)
        {
            if (LeadLocomotiveIndex >= 0)
            {
                MSTSLocomotive lead = (MSTSLocomotive)Cars[LeadLocomotiveIndex];
                if (lead.TrainBrakeController != null)
                    lead.TrainBrakeController.UpdatePressure(ref EqualReservoirPressurePSIorInHg, elapsedClockSeconds, ref BrakeLine4);
                if (lead.EngineBrakeController != null)
                    lead.EngineBrakeController.UpdateEngineBrakePressure(ref BrakeLine3PressurePSI, elapsedClockSeconds);
                lead.BrakeSystem.PropagateBrakePressure(elapsedClockSeconds);
            }
            else if (TrainType == TRAINTYPE.STATIC)
            {
                // Propagate brake pressure of locomotiveless static consists in the advanced way,
                // to allow proper shunting operations.
                Cars[0].BrakeSystem.PropagateBrakePressure(elapsedClockSeconds);
            }
            else
            {
                // Propagate brake pressure of AI trains simplified
                AISetUniformBrakePressures();
            }
        }

        /// <summary>
        /// AI trains simplyfied brake control is done by setting their Train.BrakeLine1PressurePSIorInHg,
        /// that is propagated promptly to each car directly.
        /// </summary>
        private void AISetUniformBrakePressures()
        {
            foreach (TrainCar car in Cars)
            {
                car.BrakeSystem.BrakeLine1PressurePSI = car.BrakeSystem.InternalPressure(EqualReservoirPressurePSIorInHg);
                car.BrakeSystem.BrakeLine2PressurePSI = BrakeLine2PressurePSI;
                car.BrakeSystem.BrakeLine3PressurePSI = 0;
            }
        }

        //================================================================================================//
        /// <summary>
        /// Cars have been added to the rear of the train, recalc the rearTDBtraveller
        /// </summary>

        public void RepositionRearTraveller()
        {
            var traveller = new Traveller(FrontTDBTraveller, Traveller.TravellerDirection.Backward);
            // The traveller location represents the front of the train.
            var length = 0f;

            // process the cars first to last
            for (var i = 0; i < Cars.Count; ++i)
            {
                var car = Cars[i];
                if (car.WheelAxlesLoaded)
                {
                    car.ComputePosition(traveller, false, 0, 0, SpeedMpS);
                }
                else
                {
                    var bogieSpacing = car.CarLengthM * 0.65f;  // we'll use this approximation since the wagfile doesn't contain info on bogie position

                    // traveller is positioned at the front of the car
                    // advance to the first bogie 
                    traveller.Move((car.CarLengthM - bogieSpacing) / 2.0f);
                    var tileX = traveller.TileX;
                    var tileZ = traveller.TileZ;
                    var x = traveller.X;
                    var y = traveller.Y;
                    var z = traveller.Z;
                    traveller.Move(bogieSpacing);

                    // normalize across tile boundaries
                    while (tileX > traveller.TileX)
                    {
                        x += 2048;
                        --tileX;
                    }
                    while (tileX < traveller.TileX)
                    {
                        x -= 2048;
                        ++tileX;
                    }
                    while (tileZ > traveller.TileZ)
                    {
                        z += 2048;
                        --tileZ;
                    }
                    while (tileZ < traveller.TileZ)
                    {
                        z -= 2048;
                        ++tileZ;
                    }

                    // note the railcar sits 0.275meters above the track database path  TODO - is this always consistent?
                    car.WorldPosition.XNAMatrix = Matrix.Identity;
                    if (!car.Flipped)
                    {
                        //  Rotate matrix 180' around Y axis.
                        car.WorldPosition.XNAMatrix.M11 = -1;
                        car.WorldPosition.XNAMatrix.M33 = -1;
                    }
                    car.WorldPosition.XNAMatrix *= Simulator.XNAMatrixFromMSTSCoordinates(traveller.X, traveller.Y + 0.275f, traveller.Z, x, y + 0.275f, z);
                    car.WorldPosition.TileX = traveller.TileX;
                    car.WorldPosition.TileZ = traveller.TileZ;

                    traveller.Move((car.CarLengthM - bogieSpacing) / 2.0f);
                }
                if (i < Cars.Count - 1)
                {
                    traveller.Move(car.CouplerSlackM + car.GetCouplerZeroLengthM());
                    length += car.CouplerSlackM + car.GetCouplerZeroLengthM();
                }
                length += car.CarLengthM;
            }

            traveller.ReverseDirection();
            RearTDBTraveller = traveller;
            Length = length;
        } // RepositionRearTraveller


        //================================================================================================//
        /// <summary>
        /// Check if train is passenger or freight train
        /// </summary>

        public void CheckFreight()
        {
            IsFreight = false;
            PassengerCarsNumber = 0;
            IsPlayable = false;
            foreach (var car in Cars)
            {
                if (car.WagonType == TrainCar.WagonTypes.Freight)
                    IsFreight = true;
                if ((car.WagonType == TrainCar.WagonTypes.Passenger) || (car.IsDriveable && car.HasPassengerCapacity))
                    PassengerCarsNumber++;
                if (car.IsDriveable && (car as MSTSLocomotive).CabViewList.Count > 0) IsPlayable = true;
            }
            if (TrainType == TRAINTYPE.AI_INCORPORATED && IncorporatingTrainNo > -1) IsPlayable = true;
        } // CheckFreight

        public void CalculatePositionOfCars()
        {
            CalculatePositionOfCars(0, 0);
        }

        //================================================================================================//
        /// <summary>
        /// Distance is the signed distance the cars are moving.
        /// </summary>
        /// <param name="distance"></param>

        public void CalculatePositionOfCars(float elapsedTime, float distance)
        {
            if (float.IsNaN(distance)) distance = 0;//sanity check

            RearTDBTraveller.Move(distance);

            // TODO : check if train moved back into previous section

            var traveller = new Traveller(RearTDBTraveller);
            // The traveller location represents the back of the train.
            var length = 0f;

            // process the cars last to first
            for (var i = Cars.Count - 1; i >= 0; --i)
            {
                var car = Cars[i];
                if (i < Cars.Count - 1)
                {
                    traveller.Move(car.CouplerSlackM + car.GetCouplerZeroLengthM());
                    length += car.CouplerSlackM + car.GetCouplerZeroLengthM();
                }
                if (car.WheelAxlesLoaded)
                {
                    car.ComputePosition(traveller, true, elapsedTime, distance, SpeedMpS);
                }
                else
                {
                    var bogieSpacing = car.CarLengthM * 0.65f;  // we'll use this approximation since the wagfile doesn't contain info on bogie position

                    // traveller is positioned at the back of the car
                    // advance to the first bogie 
                    traveller.Move((car.CarLengthM - bogieSpacing) / 2.0f);
                    var tileX = traveller.TileX;
                    var tileZ = traveller.TileZ;
                    var x = traveller.X;
                    var y = traveller.Y;
                    var z = traveller.Z;
                    traveller.Move(bogieSpacing);

                    // normalize across tile boundaries
                    while (tileX > traveller.TileX)
                    {
                        x += 2048;
                        --tileX;
                    }
                    while (tileX < traveller.TileX)
                    {
                        x -= 2048;
                        ++tileX;
                    }
                    while (tileZ > traveller.TileZ)
                    {
                        z += 2048;
                        --tileZ;
                    }
                    while (tileZ < traveller.TileZ)
                    {
                        z -= 2048;
                        ++tileZ;
                    }


                    // note the railcar sits 0.275meters above the track database path  TODO - is this always consistent?
                    car.WorldPosition.XNAMatrix = Matrix.Identity;
                    if (car.Flipped)
                    {
                        //  Rotate matrix 180' around Y axis.
                        car.WorldPosition.XNAMatrix.M11 = -1;
                        car.WorldPosition.XNAMatrix.M33 = -1;
                    }
                    car.WorldPosition.XNAMatrix *= Simulator.XNAMatrixFromMSTSCoordinates(traveller.X, traveller.Y + 0.275f, traveller.Z, x, y + 0.275f, z);
                    car.WorldPosition.TileX = traveller.TileX;
                    car.WorldPosition.TileZ = traveller.TileZ;

                    traveller.Move((car.CarLengthM - bogieSpacing) / 2.0f);  // Move to the front of the car 

                    car.UpdatedTraveler(traveller, elapsedTime, distance, SpeedMpS);
                }
                length += car.CarLengthM;
            }

            FrontTDBTraveller = traveller;
            Length = length;
            travelled += distance;
        } // CalculatePositionOfCars

        //================================================================================================//
        /// <summary>
        ///  Sets this train's speed so that momentum is conserved when otherTrain is coupled to it
        /// <\summary>

        public void SetCoupleSpeed(Train otherTrain, float otherMult)
        {
            float kg1 = 0;
            foreach (TrainCar car in Cars)
                kg1 += car.MassKG;
            float kg2 = 0;
            foreach (TrainCar car in otherTrain.Cars)
                kg2 += car.MassKG;
            SpeedMpS = (kg1 * SpeedMpS + kg2 * otherTrain.SpeedMpS * otherMult) / (kg1 + kg2);
            otherTrain.SpeedMpS = SpeedMpS;
            foreach (TrainCar car1 in Cars)
                //TODO: next code line has been modified to flip trainset physics in order to get viewing direction coincident with loco direction when using rear cab.
                // To achieve the same result with other means, without flipping trainset physics, the line should be changed as follows:
                //                 car1.SpeedMpS = car1.Flipped ? -SpeedMpS : SpeedMpS;
                car1.SpeedMpS = car1.Flipped ^ (car1.IsDriveable && car1.Train.IsActualPlayerTrain && ((MSTSLocomotive)car1).UsingRearCab) ? -SpeedMpS : SpeedMpS;
            foreach (TrainCar car2 in otherTrain.Cars)
                //TODO: next code line has been modified to flip trainset physics in order to get viewing direction coincident with loco direction when using rear cab.
                // To achieve the same result with other means, without flipping trainset physics, the line should be changed as follows:
                //                 car2.SpeedMpS = car2.Flipped ? -SpeedMpS : SpeedMpS;
                car2.SpeedMpS = car2.Flipped ^ (car2.IsDriveable && car2.Train.IsActualPlayerTrain && ((MSTSLocomotive)car2).UsingRearCab) ? -SpeedMpS : SpeedMpS;
        }


        //================================================================================================//
        /// <summary>
        /// setups of the left hand side of the coupler force solving equations
        /// <\summary>

        void SetupCouplerForceEquations()
        {
            for (int i = 0; i < Cars.Count - 1; i++)
            {
                TrainCar car = Cars[i];
                car.CouplerForceB = 1 / car.MassKG;
                car.CouplerForceA = -car.CouplerForceB;
                car.CouplerForceC = -1 / Cars[i + 1].MassKG;
                car.CouplerForceB -= car.CouplerForceC;
            }
        }


        //================================================================================================//
        /// <summary>
        /// solves coupler force equations
        /// <\summary>

        void SolveCouplerForceEquations()
        {
            float b = Cars[0].CouplerForceB;
            Cars[0].CouplerForceU = Cars[0].CouplerForceR / b;


            for (int i = 1; i < Cars.Count - 1; i++)
            {
                Cars[i].CouplerForceG = Cars[i - 1].CouplerForceC / b;
                b = Cars[i].CouplerForceB - Cars[i].CouplerForceA * Cars[i].CouplerForceG;
                Cars[i].CouplerForceU = (Cars[i].CouplerForceR - Cars[i].CouplerForceA * Cars[i - 1].CouplerForceU) / b;
            }

            for (int i = Cars.Count - 3; i >= 0; i--)
            {
                Cars[i].CouplerForceU -= Cars[i + 1].CouplerForceG * Cars[i + 1].CouplerForceU;
            }
                
        }


        //================================================================================================//
        /// <summary>
        /// removes equations if forces don't match faces in contact
        /// returns true if a change is made
        /// <\summary>

        bool FixCouplerForceEquations()
        {

            // coupler in tension
            for (int i = 0; i < Cars.Count - 1; i++)
            {
                TrainCar car = Cars[i];

            // if coupler in compression on this car, or coupler is not to be solved, then jump car
                if (car.CouplerSlackM < 0 || car.CouplerForceB >= 1) 
                    continue;

                if (Simulator.UseAdvancedAdhesion && car.IsAdvancedCoupler) // "Advanced coupler" - operates in three extension zones
                {
                    float maxs0 = car.GetMaximumCouplerSlack0M();

                    if (car.CouplerSlackM < maxs0 )
 //               if (car.CouplerSlackM < maxs0 || car.CouplerForceU > 0)  // In Zone 1 set coupler forces to zero, as coupler faces not touching, or if coupler force is in the opposite direction, ie compressing ( +ve CouplerForceU )
                    {
//                        Trace.TraceInformation("FixCoupler #1 - Tension - CardId {0} SlackM {1} Slack2M {2} Maxs0 {3} Force {4}", car.CarID, car.CouplerSlackM, car.CouplerSlack2M, maxs0, car.CouplerForceU);
                        SetCouplerForce(car, 0);
                        return true;
                    }
                }
                else // "Simple coupler" - operates on two extension zones, coupler faces not in contact, and coupler fuller in contact
                {
                    float maxs1 = car.GetMaximumCouplerSlack1M();
                    // In Zone 1 set coupler forces to zero, as coupler faces not touching, or if coupler force is in the opposite direction, ie compressing ( +ve CouplerForceU )
                    if (car.CouplerSlackM < maxs1 || car.CouplerForceU > 0) 
                    {
                        SetCouplerForce(car, 0);
                        return true;
                    }
                }
            }


           // Coupler in compression
            for (int i = Cars.Count - 1; i >= 0; i--)
            {
                TrainCar car = Cars[i];

                // Coupler in tension on this car or coupler force is "zero" then jump to next car
                if (car.CouplerSlackM > 0 || car.CouplerForceB >= 1)
                    continue;

                if (Simulator.UseAdvancedAdhesion && car.IsAdvancedCoupler) // "Advanced coupler" - operates in three extension zones
                {
                    float maxs0 = car.GetMaximumCouplerSlack0M();

                    if (car.CouplerSlackM > -maxs0)
//                    if (car.CouplerSlackM > -maxs0 || car.CouplerForceU < 0) // In Zone 1 set coupler forces to zero, as coupler faces not touching, or if coupler force is in the opposite direction, ie in tension ( -ve CouplerForceU )
                    {
//                        Trace.TraceInformation("FixCoupler #2 - Compression - CardId {0} SlackM {1} Slack2M {2} Maxs0 {3} Force {4}", car.CarID, car.CouplerSlackM, car.CouplerSlack2M, maxs0, car.CouplerForceU);
                        SetCouplerForce(car, 0);
                        return true;
                    }
                }
                else // "Simple coupler" - operates on two extension zones, coupler faces not in contact, and coupler fuller in contact
                {

                    float maxs1 = car.GetMaximumCouplerSlack1M();
                    // In Zone 1 set coupler forces to zero, as coupler faces not touching, or if coupler force is in the opposite direction, ie in tension ( -ve CouplerForceU )
                    if (car.CouplerSlackM > -maxs1 || car.CouplerForceU < 0) 
                    {
                        SetCouplerForce(car, 0);
                        return true;
                    }
                }
            }
            return false;
        }


        //================================================================================================//
        /// <summary>
        /// changes the coupler force equation for car to make the corresponding force equal to forceN
        /// <\summary>

        static void SetCouplerForce(TrainCar car, float forceN)
        {
            car.CouplerForceA = car.CouplerForceC = 0;
            car.CouplerForceB = 1;
            car.CouplerForceR = forceN;
        }

        //================================================================================================//
        /// <summary>
        /// removes equations if forces don't match faces in contact
        /// returns true if a change is made
        /// <\summary>

        bool FixCouplerImpulseForceEquations()
        {
            // coupler in tension - CouplerForce -ve
            for (int i = 0; i < Cars.Count - 1; i++)
            {
                TrainCar car = Cars[i];
                if (car.CouplerSlackM < 0 || car.CouplerForceB >= 1)
                    continue;
                if (car.CouplerSlackM < car.CouplerSlack2M || car.CouplerForceU > 0)
                {
 //                   Trace.TraceInformation("FixCouplerImpulse #1 - Tension - CardId {0} SlackM {1} Slack2M {2} Force {3}", car.CarID, car.CouplerSlackM, car.CouplerSlack2M, car.CouplerForceU);
                    SetCouplerForce(car, 0);
                    return true;
                }
            }

            // Coupler in compression - CouplerForce +ve
            for (int i = Cars.Count - 1; i >= 0; i--)
            {
                TrainCar car = Cars[i];
                if (car.CouplerSlackM > 0 || car.CouplerForceB >= 1)
                    continue;
                if (car.CouplerSlackM > -car.CouplerSlack2M || car.CouplerForceU < 0)
                {
//                    Trace.TraceInformation("FixCouplerImpulse #2 - Tension - CardId {0} SlackM {1} Slack2M {2} Force {3}", car.CarID, car.CouplerSlackM, car.CouplerSlack2M, car.CouplerForceU);
                    SetCouplerForce(car, 0);
                    return true;
                }
            }
            return false;
        }


        //================================================================================================//
        /// <summary>
        /// computes and applies coupler impulse forces which force speeds to match when no relative movement is possible
        /// <\summary>

        public void AddCouplerImpulseForces()
        {
            if (Cars.Count < 2)
                return;
            SetupCouplerForceEquations();

            for (int i = 0; i < Cars.Count - 1; i++)
            {
                TrainCar car = Cars[i];

                    float max = car.CouplerSlack2M;
                    if (-max < car.CouplerSlackM && car.CouplerSlackM < max)
                    {
                        car.CouplerForceB = 1;
                        car.CouplerForceA = car.CouplerForceC = car.CouplerForceR = 0;
                    }
                    else
                        car.CouplerForceR = Cars[i + 1].SpeedMpS - car.SpeedMpS;
            }

            do
                SolveCouplerForceEquations();
            while (FixCouplerImpulseForceEquations());
            MaximumCouplerForceN = 0;
            for (int i = 0; i < Cars.Count - 1; i++)
            {
                Cars[i].SpeedMpS += Cars[i].CouplerForceU / Cars[i].MassKG;
                Cars[i + 1].SpeedMpS -= Cars[i].CouplerForceU / Cars[i + 1].MassKG;
                //if (Cars[i].CouplerForceU != 0)
                //    Console.WriteLine("impulse {0} {1} {2} {3} {4}", i, Cars[i].CouplerForceU, Cars[i].CouplerSlackM, Cars[i].SpeedMpS, Cars[i+1].SpeedMpS);
                //if (MaximumCouplerForceN < Math.Abs(Cars[i].CouplerForceU))
                //    MaximumCouplerForceN = Math.Abs(Cars[i].CouplerForceU);
            }
        }

        //================================================================================================//
        /// <summary>
        /// computes coupler acceleration balancing forces for Coupler
        /// The couplers are calculated using the formulas 9.7 to 9.9 (pg 243), described in the Handbook of Railway Vehicle Dynamics by Simon Iwnicki
        ///  In the book there is one equation per car and in OR there is one equation per coupler. To get the OR equations, first solve the 
        ///  equations in the book for acceleration. Then equate the acceleration equation for each pair of adjacent cars. Arrange the fwc 
        ///  terms on the left hand side and all other terms on the right side. Now if the fwc values are treated as unknowns, there is a 
        ///  tridiagonal system of linear equations which can be solved to find the coupler forces needed to make the accelerations match.
        ///  
        ///  Each fwc value corresponds to one of the CouplerForceU values.The CouplerForceA, CouplerForceB and CouplerForceC values are 
        ///  the CouplerForceU coefficients for the previuous coupler, the current coupler and the next coupler.The CouplerForceR values are 
        ///  the sum of the right hand side terms. The notation and the code in SolveCouplerForceEquations() that solves for the CouplerForceU 
        ///  values is from "Numerical Recipes in C".
        ///  
        /// Or has two coupler models - Simple and Advanced
        /// Simple - has two extension zones - #1 where the coupler faces have not come into contact, and hence CouplerForceU is zero, #2 where coupler 
        /// forces are taking the full weight of the following car. The breaking capacity of the coupler could be considered zone 3
        /// 
        /// Advanced - has three extension zones, and the breaking zone - #1 where the coupler faces have not come into contact, and hence 
        /// CouplerForceU is zero, #2 where the spring is taking the load, and car is able to oscilate in the train as it moves backwards and 
        /// forwards due to the action of the spring, #3 - where the coupler is fully extended against the friction brake, and the full force of the 
        /// following wagons will be applied to the coupler.
        /// 
        /// <\summary>

        public void ComputeCouplerForces()
        {

                // TODO: this loop could be extracted and become a separate method, that could be called also by TTTrain.physicsPreUpdate
                for (int i = 0; i < Cars.Count; i++)
                {
                    if (Cars[i].SpeedMpS > 0)
                        Cars[i].TotalForceN -= (Cars[i].FrictionForceN + Cars[i].BrakeForceN + Cars[i].CurveForceN + Cars[i].WindForceN + Cars[i].TunnelForceN +
                            ((Cars[i] is MSTSLocomotive && (Cars[i] as MSTSLocomotive).DynamicBrakeForceN > 0) ? Math.Abs(Cars[i].MotiveForceN) : 0));
                    else if (Cars[i].SpeedMpS < 0)
                        Cars[i].TotalForceN += Cars[i].FrictionForceN + Cars[i].BrakeForceN + Cars[i].CurveForceN + Cars[i].WindForceN + Cars[i].TunnelForceN +
                            ((Cars[i] is MSTSLocomotive && (Cars[i] as MSTSLocomotive).DynamicBrakeForceN > 0) ? Math.Abs(Cars[i].MotiveForceN) : 0);
                }

                if (Cars.Count < 2)
                    return;

                SetupCouplerForceEquations(); // Based upon the car Mass, set up LH side forces (ABC) parameters

                // Calculate RH side coupler force
                // Whilever coupler faces not in contact, then "zero coupler force" by setting A = C = R = 0
                // otherwise R is calculated based on difference in acceleration between cars, or stiffness and damping value
                for (int i = 0; i < Cars.Count - 1; i++)
                {
                        TrainCar car = Cars[i];
                    if (Simulator.UseAdvancedAdhesion && car.IsAdvancedCoupler) // "Advanced coupler" - operates in three extension zones
                    {
                    float max0 = car.GetMaximumCouplerSlack0M();
                    float max1 = car.GetMaximumCouplerSlack1M();

                    if ( car.CouplerSlackM > -max0 && car.CouplerSlackM < max0) // Zone 1 coupler faces not in contact - no force generated
                    {
                        car.CouplerForceB = 1;
                        car.CouplerForceA = car.CouplerForceC = car.CouplerForceR = 0;
                    }
                    else if (-max1 < car.CouplerSlackM && car.CouplerSlackM < -max0 || car.CouplerSlackM > max0 && car.CouplerSlackM < max1)   // Zone 2 coupler faces in contact, but spring and damping effects are in play
                    {
                        car.CouplerForceR = (Math.Abs(car.CouplerSlackM) * car.GetCouplerStiffness1NpM() + car.CouplerDampingSpeedMpS * car.GetCouplerDamping1NMpS()) / Cars[i + 1].MassKG - car.TotalForceN / car.MassKG;
                    }
                    else // Zone 3 coupler faces fully in contact - full force generated
                    {
                        car.CouplerForceR = Cars[i + 1].TotalForceN / Cars[i + 1].MassKG - car.TotalForceN / car.MassKG;
                    }

                }
                    else // "Simple coupler" - operates on two extension zones, coupler faces not in contact, and coupler fuller in contact
                    {
                        float max = car.GetMaximumCouplerSlack1M();
                        if (-max < car.CouplerSlackM && car.CouplerSlackM < max)
                        {
                            car.CouplerForceB = 1;
                            car.CouplerForceA = car.CouplerForceC = car.CouplerForceR = 0;
                        }
                        else
                            car.CouplerForceR = Cars[i + 1].TotalForceN / Cars[i + 1].MassKG - car.TotalForceN / car.MassKG;
                    }
                }

                // Solve coupler forces to find CouplerForceU
                do
                    SolveCouplerForceEquations();
                while (FixCouplerForceEquations());

                for (int i = 0; i < Cars.Count - 1; i++)
                {
                    // Calculate total forces on cars
                    TrainCar car = Cars[i];
                    car.TotalForceN += car.CouplerForceU;
                    Cars[i + 1].TotalForceN -= car.CouplerForceU;

                    // Find max coupler force on the car - currently doesn't appear to be used anywhere
                    if (MaximumCouplerForceN < Math.Abs(car.CouplerForceU))
                        MaximumCouplerForceN = Math.Abs(car.CouplerForceU);

                    // Update couplerslack2m which acts as an upper limit in slack calculations
                    float maxs = car.GetMaximumCouplerSlack2M();

                if (Simulator.UseAdvancedAdhesion && car.IsAdvancedCoupler) // "Advanced coupler" - operates in three extension zones
                {
                             car.CouplerSlack2M = maxs;
                }
                else
                {
                    if (car.CouplerForceU > 0)
                    {
                        float f = -(car.CouplerSlackM + car.GetMaximumCouplerSlack1M()) * car.GetCouplerStiffnessNpM();
                        if (car.CouplerSlackM > -maxs && f > car.CouplerForceU)
                            car.CouplerSlack2M = -car.CouplerSlackM;
                        else
                            car.CouplerSlack2M = maxs;
                    }
                    else if (car.CouplerForceU == 0)
                        car.CouplerSlack2M = maxs;
                    else
                    {
                        float f = (car.CouplerSlackM - car.GetMaximumCouplerSlack1M()) * car.GetCouplerStiffnessNpM();
                        if (car.CouplerSlackM < maxs && f > car.CouplerForceU)
                            car.CouplerSlack2M = car.CouplerSlackM;
                        else
                            car.CouplerSlack2M = maxs;
                    }
                }
            }
        }

        //================================================================================================//
        /// <summary>
        /// Update Car speeds
        /// <\summary>

        public void UpdateCarSpeeds(float elapsedTime)
        {
            // The train speed is calculated by averaging all the car speeds. The individual car speeds are calculated from the TotalForce acting on each car. Typically the MotiveForce or Gravitational forces (though other forces like friction have a small impact as well).
            // At stop under normal circumstances the BrakeForce exceeds the TotalForces, and therefore the wagon is "held in a stationary position". 
            // In the case of "air_piped" wagons which have no BrakeForces acting on them, the car is not held stationary, and each car shows a small speed.
            // To overcome this any "air_piped cars are forced to zero speed if the preceeding car is stationary.
            int n = 0;
            float PrevCarSpeedMps = 0.0f;
            float NextCarSpeedMps = 0.0f;
            bool locoBehind = true;
            for (int iCar = 0; iCar < Cars.Count; iCar++)
            {
                var car = Cars[iCar];
                if (iCar < Cars.Count - 1) NextCarSpeedMps = Cars[iCar + 1].SpeedMpS;
                if (TrainMaxSpeedMpS <= 0f)
                {
                    if (car is MSTSLocomotive)
                        TrainMaxSpeedMpS = (car as MSTSLocomotive).MaxSpeedMpS;
                    if (car is MSTSElectricLocomotive)
                        TrainMaxSpeedMpS = (car as MSTSElectricLocomotive).MaxSpeedMpS;
                    if (car is MSTSDieselLocomotive)
                        TrainMaxSpeedMpS = (car as MSTSDieselLocomotive).MaxSpeedMpS;
                    if (car is MSTSSteamLocomotive)
                        TrainMaxSpeedMpS = (car as MSTSSteamLocomotive).MaxSpeedMpS;
                }
                if (car is MSTSLocomotive) locoBehind = false;
                if (car.SpeedMpS > 0)
                {
                    car.SpeedMpS += car.TotalForceN / car.MassKG * elapsedTime;
                    if (car.SpeedMpS < 0)
                        car.SpeedMpS = 0;
                    // If is "air_piped car, and preceeding car is at stop, then set speed to zero.
                    if ((car.CarBrakeSystemType == "air_piped" || car.CarBrakeSystemType == "vacuum_piped") && (locoBehind ? n != Cars.Count - 1 && NextCarSpeedMps == 0 : n != 0 && PrevCarSpeedMps == 0))
                    {
                        car.SpeedMpS = 0;
                    }
                    PrevCarSpeedMps = car.SpeedMpS;
                }
                else if (car.SpeedMpS < 0)
                {
                    car.SpeedMpS += car.TotalForceN / car.MassKG * elapsedTime;
                    if (car.SpeedMpS > 0)
                        car.SpeedMpS = 0;
                    // If is "air_piped car, and preceeding is at stop, then set speed to zero.
                    if ((car.CarBrakeSystemType == "air_piped" || car.CarBrakeSystemType == "vacuum_piped") && (locoBehind ? n != Cars.Count - 1 && NextCarSpeedMps == 0 : n != 0 && PrevCarSpeedMps == 0))
                    {
                        car.SpeedMpS = 0;
                    }
                    PrevCarSpeedMps = car.SpeedMpS;
                }
                else // if speed equals zero
                    PrevCarSpeedMps = car.SpeedMpS;
                n++;
#if DEBUG_SPEED_FORCES
                Trace.TraceInformation(" ========================================  Train Speed #2 (Train.cs) ===========================================================");
                Trace.TraceInformation("Car ID {0} TotalForceN {1} Mass {2} elapsedtime {3} CarSpeed {4}", car.CarID, car.TotalForceN, car.MassKG, elapsedTime, car.SpeedMpS);
                Trace.TraceInformation("Friction {0} Brake {1} Curve {2} Wind {3} Tunnel {4}", car.FrictionForceN, car.BrakeForceN, car.CurveForceN, car.WindForceN, car.TunnelForceN);
                Trace.TraceInformation("Coupler {0} Prev Car Speed {1}", car.CouplerForceU, PrevCarSpeedMps);
                Trace.TraceInformation("Calculated Total {0}", car.FrictionForceN + car.BrakeForceN + car.CurveForceN + car.WindForceN + car.TunnelForceN);
#endif
            }
            if (n == 0)
                return;
            float PrevMovingCarSpeedMps = 0.0f;
            // start cars moving forward

            for (int i = 0; i < Cars.Count; i++)
            {
                TrainCar car = Cars[i];
                if (car.SpeedMpS != 0 || car.TotalForceN <= (car.FrictionForceN + car.BrakeForceN + car.CurveForceN + car.WindForceN + car.TunnelForceN + 
                    ((car is MSTSLocomotive && (car as MSTSLocomotive).DynamicBrakeForceN > 0) ? Math.Abs(car.MotiveForceN) : 0)))
                    continue;
                int j = i;
                float f = 0;
                float m = 0;
                for (;;)
                {
                    if (car is MSTSLocomotive)
                    {
                        f += car.TotalForceN - (car.FrictionForceN + car.CurveForceN + car.WindForceN + car.TunnelForceN);
                        if ((car as MSTSLocomotive).DynamicBrakeForceN > 0)
                        {
                            f -= Math.Abs(car.MotiveForceN);
                        }
                    }
                    else
                        f += car.TotalForceN - (car.FrictionForceN + car.BrakeForceN + car.CurveForceN + car.WindForceN + car.TunnelForceN);
                    m += car.MassKG;
                    if (j == Cars.Count - 1 || car.CouplerSlackM < car.GetMaximumCouplerSlack2M())
                        break;
                    j++;
                    car = Cars[j];
                }
                if (f > 0)
                {
                    for (int k = i; k <= j; k++)
                    {
                        // If is "air_piped car, and preceeding car is at stop, then set speed to zero.
                        if ((Cars[k].CarBrakeSystemType == "air_piped" || Cars[k].CarBrakeSystemType == "vacuum_piped") && PrevMovingCarSpeedMps == 0.0)
                        {
                            Cars[k].SpeedMpS = 0.0f;
                        }
                        else
                        {
                            Cars[k].SpeedMpS = f / m * elapsedTime;
                        }
                        PrevMovingCarSpeedMps = Cars[k].SpeedMpS;
                    }
                    n -= j - i + 1;
                }
            }
            if (n == 0)
                return;



            // start cars moving backward
            for (int i = Cars.Count - 1; i >= 0; i--)
            {
                TrainCar car = Cars[i];
                if (car.SpeedMpS != 0 || car.TotalForceN > (-1.0f * (car.FrictionForceN + car.BrakeForceN + car.CurveForceN + car.WindForceN + car.TunnelForceN+ 
                    ((car.IsDriveable && (car as MSTSLocomotive).DynamicBrakeForceN > 0) ? Math.Abs(car.MotiveForceN) : 0))))
                    continue;
                int j = i;
                float f = 0;
                float m = 0;
                for (;;)
                {
                    if (car is MSTSLocomotive)
                    {
                        f += car.TotalForceN + car.FrictionForceN + car.CurveForceN + car.WindForceN + car.TunnelForceN;
                        if ((car as MSTSLocomotive).DynamicBrakeForceN > 0)
                        {
                            f += Math.Abs(car.MotiveForceN);
                        }
                    }
                    else
                        f += car.TotalForceN + car.FrictionForceN + car.BrakeForceN + car.CurveForceN + car.WindForceN + car.TunnelForceN;
                    m += car.MassKG;
                    if (j == 0 || car.CouplerSlackM > -car.GetMaximumCouplerSlack2M())
                        break;
                    j--;
                    car = Cars[j];
                }
                if (f < 0)
                {
                    for (int k = j; k <= i; k++)
                    {
                        // If is "air_piped car, and preceeding car is at stop, then set speed to zero.
                        if ((Cars[k].CarBrakeSystemType == "air_piped" || Cars[k].CarBrakeSystemType == "vacuum_piped") && PrevMovingCarSpeedMps == 0.0)
                        {
                            Cars[k].SpeedMpS = 0.0f;
                        }
                        else
                        {
                            Cars[k].SpeedMpS = f / m * elapsedTime;
                        }
                        PrevMovingCarSpeedMps = Cars[k].SpeedMpS;
                    }
                }
            }
        }

        //================================================================================================//
        /// <summary>
        /// Update coupler slack - ensures that coupler slack doesn't exceed the maximum permissible value, and provides indication to HUD
        /// <\summary>

        public void UpdateCouplerSlack(float elapsedTime)
        {
            TotalCouplerSlackM = 0;
            NPull = NPush = 0;
            for (int i = 0; i < Cars.Count - 1; i++)
            {
                // update coupler slack distance
                TrainCar car = Cars[i];
                car.CouplerSlackM += (car.SpeedMpS - Cars[i + 1].SpeedMpS) * elapsedTime;

                // Calculate speed for damping force
                car.CouplerDampingSpeedMpS = car.SpeedMpS - Cars[i + 1].SpeedMpS;

                // Make sure that coupler slack does not exceed the maximum coupler slack
                float max = car.GetMaximumCouplerSlack2M();
                if (car.CouplerSlackM < -max)
                    car.CouplerSlackM = -max;
                else if (car.CouplerSlackM > max)
                    car.CouplerSlackM = max;

                TotalCouplerSlackM += car.CouplerSlackM; // Total coupler slack displayed in HUD only

//                Trace.TraceInformation("Slack - CarID {0} Slack {1} Zero {2} MaxSlack0 {3} MaxSlack1 {4} MaxSlack2 {5} Damping1 {6} Damping2 {7} Stiffness1 {8} Stiffness2 {9} AdvancedCpl {10} CplSlackA {11} CplSlackB {12}", 
//                    car.CarID, car.CouplerSlackM, car.GetCouplerZeroLengthM(), car.GetMaximumCouplerSlack0M(),
//                    car.GetMaximumCouplerSlack1M(), car.GetMaximumCouplerSlack2M(), car.GetCouplerDamping1NMpS(), car.GetCouplerDamping2NMpS(), 
//                    car.GetCouplerStiffness1NpM(), car.GetCouplerStiffness1NpM(), car.IsAdvancedCoupler, car.GetCouplerSlackAM(), car.GetCouplerSlackBM());

                if (car.CouplerSlackM >= 0.001) // Coupler pulling
                {
                    NPull++;
                    car.HUDCouplerForceIndication = 1; 
                }                    
                else if (car.CouplerSlackM <= -0.001)
                {
                    NPush++;
                    car.HUDCouplerForceIndication = 2;
                }
                else
                {
                    car.HUDCouplerForceIndication = 0;
                }
                    
            }
            foreach (TrainCar car in Cars)
                car.DistanceM += Math.Abs(car.SpeedMpS * elapsedTime);
        }

        //================================================================================================//
        /// <summary>
        /// Calculate initial position
        /// </summary>

        public virtual TCSubpathRoute CalculateInitialTrainPosition(ref bool trackClear)
        {

            // calculate train length

            float trainLength = 0f;

            for (var i = Cars.Count - 1; i >= 0; --i)
            {
                var car = Cars[i];
                if (i < Cars.Count - 1)
                {
                    trainLength += car.CouplerSlackM + car.GetCouplerZeroLengthM();
                }
                trainLength += car.CarLengthM;
            }

            // get starting position and route

            TrackNode tn = RearTDBTraveller.TN;
            float offset = RearTDBTraveller.TrackNodeOffset;
            int direction = (int)RearTDBTraveller.Direction;

            PresentPosition[1].SetTCPosition(tn.TCCrossReference, offset, direction);
            TrackCircuitSection thisSection = signalRef.TrackCircuitList[PresentPosition[1].TCSectionIndex];
            offset = PresentPosition[1].TCOffset;

            //<CSComment> must do preliminary calculation of PresentPosition[0] parameters in order to use subsequent code
            // limited however to case of train fully in one section to avoid placement ambiguities </CSComment>
            float offsetFromEnd = thisSection.Length - (Length + offset);
            if (PresentPosition[0].TCSectionIndex == -1 && offsetFromEnd >= 0) // train is fully in one section
            {
                PresentPosition[0].TCDirection = PresentPosition[1].TCDirection;
                PresentPosition[0].TCSectionIndex = PresentPosition[1].TCSectionIndex;
                PresentPosition[0].TCOffset = PresentPosition[1].TCOffset + trainLength;
            }

            // create route if train has none

            if (ValidRoute[0] == null)
            {
                ValidRoute[0] = signalRef.BuildTempRoute(this, thisSection.Index, PresentPosition[1].TCOffset,
                            PresentPosition[1].TCDirection, trainLength, true, true, false);
            }

            // find sections

            bool sectionAvailable = true;
            float remLength = trainLength;
            int routeIndex = ValidRoute[0].GetRouteIndex(PresentPosition[1].TCSectionIndex, 0);
            if (routeIndex < 0)
                routeIndex = 0;

            bool sectionsClear = true;

            TCSubpathRoute tempRoute = new TCSubpathRoute();

            TCRouteElement thisElement = ValidRoute[0][routeIndex];
            thisSection = signalRef.TrackCircuitList[thisElement.TCSectionIndex];
            if (!thisSection.CanPlaceTrain(this, offset, remLength))
            {
                sectionsClear = false;
            }

            while (remLength > 0 && sectionAvailable)
            {
                tempRoute.Add(thisElement);
                remLength -= (thisSection.Length - offset);
                offset = 0.0f;

                if (remLength > 0)
                {
                    if (routeIndex < ValidRoute[0].Count - 1)
                    {
                        routeIndex++;
                        thisElement = ValidRoute[0][routeIndex];
                        thisSection = signalRef.TrackCircuitList[thisElement.TCSectionIndex];
                        if (!thisSection.CanPlaceTrain(this, offset, remLength))
                        {
                            sectionsClear = false;
                        }
                        offset = 0.0f;
                    }
                    else
                    {
                        Trace.TraceWarning("Not sufficient track to place train {0} , service name {1} ", Number, Name);
                        sectionAvailable = false;
                    }
                }

            }

            trackClear = true;

            if (MPManager.IsMultiPlayer()) return (tempRoute);
            if (!sectionAvailable || !sectionsClear)
            {
                trackClear = false;
                tempRoute.Clear();
            }

            return (tempRoute);
        }

        //================================================================================================//
        //
        // Set initial train route
        //

        public void SetInitialTrainRoute(TCSubpathRoute tempRoute)
        {

            // reserve sections, use direction 0 only

            foreach (TCRouteElement thisElement in tempRoute)
            {
                TrackCircuitSection thisSection = signalRef.TrackCircuitList[thisElement.TCSectionIndex];
                thisSection.Reserve(routedForward, tempRoute);
            }
        }

        //================================================================================================//
        //
        // Reset initial train route
        //

        public void ResetInitialTrainRoute(TCSubpathRoute tempRoute)
        {

            // unreserve sections

            foreach (TCRouteElement thisElement in tempRoute)
            {
                TrackCircuitSection thisSection = signalRef.TrackCircuitList[thisElement.TCSectionIndex];
                thisSection.RemoveTrain(this, false);
            }
        }

        //================================================================================================//
        //
        // Initial train placement
        //

        public virtual bool InitialTrainPlacement()
        {
            // for initial placement, use direction 0 only
            // set initial positions

            TrackNode tn = FrontTDBTraveller.TN;
            float offset = FrontTDBTraveller.TrackNodeOffset;
            int direction = (int)FrontTDBTraveller.Direction;

            PresentPosition[0].SetTCPosition(tn.TCCrossReference, offset, direction);
            PresentPosition[0].CopyTo(ref PreviousPosition[0]);

            DistanceTravelledM = 0.0f;

            tn = RearTDBTraveller.TN;
            offset = RearTDBTraveller.TrackNodeOffset;
            direction = (int)RearTDBTraveller.Direction;

            PresentPosition[1].SetTCPosition(tn.TCCrossReference, offset, direction);

            // check if train has route, if not create dummy

            if (ValidRoute[0] == null)
            {
                ValidRoute[0] = signalRef.BuildTempRoute(this, PresentPosition[1].TCSectionIndex, PresentPosition[1].TCOffset,
                        PresentPosition[1].TCDirection, Length, true, true, false);
            }

            // get index of first section in route

            int rearIndex = ValidRoute[0].GetRouteIndex(PresentPosition[1].TCSectionIndex, 0);
            if (rearIndex < 0)
            {
                if (CheckTrain)
                {
                    File.AppendAllText(@"C:\temp\checktrain.txt", "Start position of end of train {0} not on route " + Number);
                }
                rearIndex = 0;
            }

            PresentPosition[1].RouteListIndex = rearIndex;

            // get index of front of train

            int frontIndex = ValidRoute[0].GetRouteIndex(PresentPosition[0].TCSectionIndex, 0);
            if (frontIndex < 0)
            {
                Trace.TraceWarning("Start position of front of train {0}, service name {1} not on route ", Number, Name);
                frontIndex = 0;
            }

            PresentPosition[0].RouteListIndex = frontIndex;

            // check if train can be placed
            // get index of section in train route //

            int routeIndex = rearIndex;
            List<TrackCircuitSection> placementSections = new List<TrackCircuitSection>();

            // check if route is available

            offset = PresentPosition[1].TCOffset;
            float remLength = Length;
            bool sectionAvailable = true;

            for (int iRouteIndex = rearIndex; iRouteIndex <= frontIndex && sectionAvailable; iRouteIndex++)
            {
                TrackCircuitSection thisSection = signalRef.TrackCircuitList[ValidRoute[0][iRouteIndex].TCSectionIndex];
                if (thisSection.CanPlaceTrain(this, offset, remLength))
                {
                    placementSections.Add(thisSection);
                    remLength -= (thisSection.Length - offset);

                    if (remLength > 0)
                    {
                        if (routeIndex < ValidRoute[0].Count - 1)
                        {
                            routeIndex++;
                            TCRouteElement thisElement = ValidRoute[0][routeIndex];
                            thisSection = signalRef.TrackCircuitList[thisElement.TCSectionIndex];
                            offset = 0.0f;
                        }
                        else
                        {
                            Trace.TraceWarning("Not sufficient track to place train");
                            sectionAvailable = false;
                        }
                    }

                }
                else
                {
                    sectionAvailable = false;
                }
            }

            // if not available - return

            if (!sectionAvailable || placementSections.Count <= 0)
            {
                return (false);
            }

            // set any deadlocks for sections ahead of start with end beyond start

            for (int iIndex = 0; iIndex < rearIndex; iIndex++)
            {
                int rearSectionIndex = ValidRoute[0][iIndex].TCSectionIndex;
                if (DeadlockInfo.ContainsKey(rearSectionIndex))
                {
                    foreach (Dictionary<int, int> thisDeadlock in DeadlockInfo[rearSectionIndex])
                    {
                        foreach (KeyValuePair<int, int> thisDetail in thisDeadlock)
                        {
                            int endSectionIndex = thisDetail.Value;
                            if (ValidRoute[0].GetRouteIndex(endSectionIndex, rearIndex) >= 0)
                            {
                                TrackCircuitSection endSection = signalRef.TrackCircuitList[endSectionIndex];
                                endSection.SetDeadlockTrap(Number, thisDetail.Key);
                            }
                        }
                    }
                }
            }

            // set track occupied (if not done yet)

            foreach (TrackCircuitSection thisSection in placementSections)
            {
                if (!thisSection.IsSet(routedForward, false))
                {
                    thisSection.Reserve(routedForward, ValidRoute[0]);
                    thisSection.SetOccupied(routedForward);
                }
            }

            return (true);
        }
        //================================================================================================//
        /// <summary>
        /// Set Formed Occupied
        /// Set track occupied for train formed out of other train
        /// </summary>

        public void SetFormedOccupied()
        {

            int rearIndex = PresentPosition[1].RouteListIndex;
            int frontIndex = PresentPosition[0].RouteListIndex;

            int routeIndex = rearIndex;

            List<TrackCircuitSection> placementSections = new List<TrackCircuitSection>();

            // route is always available as previous train was there

            float offset = PresentPosition[1].TCOffset;
            float remLength = Length;

            for (int iRouteIndex = rearIndex; iRouteIndex <= frontIndex; iRouteIndex++)
            {
                TrackCircuitSection thisSection = signalRef.TrackCircuitList[ValidRoute[0][iRouteIndex].TCSectionIndex];
                placementSections.Add(thisSection);
                remLength -= (thisSection.Length - offset);

                if (remLength > 0)
                {
                    if (routeIndex < ValidRoute[0].Count - 1)
                    {
                        routeIndex++;
                        TCRouteElement thisElement = ValidRoute[0][routeIndex];
                        thisSection = signalRef.TrackCircuitList[thisElement.TCSectionIndex];
                        offset = 0.0f;
                    }
                    else
                    {
                        Trace.TraceWarning("Not sufficient track to place train");
                    }
                }
            }

            // set track occupied (if not done yet)

            foreach (TrackCircuitSection thisSection in placementSections)
            {
                if (!thisSection.IsSet(routedForward, false))
                {
                    thisSection.Reserve(routedForward, ValidRoute[0]);
                    thisSection.SetOccupied(routedForward);
                }
            }
        }

        /// <summary>
        /// Check if train is stopped in station
        /// </summary>
        /// <param name="thisPlatform"></param>
        /// <param name="stationDirection"></param>
        /// <param name="stationTCSectionIndex"></param>
        /// <returns></returns>
        public virtual bool CheckStationPosition(PlatformDetails thisPlatform, int stationDirection, int stationTCSectionIndex)
        {
            bool atStation = false;
            float platformBeginOffset = thisPlatform.TCOffset[0, stationDirection];
            float platformEndOffset = thisPlatform.TCOffset[1, stationDirection];
            int endSectionIndex = stationDirection == 0 ?
                    thisPlatform.TCSectionIndex[thisPlatform.TCSectionIndex.Count - 1] :
                    thisPlatform.TCSectionIndex[0];
            int endSectionRouteIndex = ValidRoute[0].GetRouteIndex(endSectionIndex, 0);

            int beginSectionIndex = stationDirection == 1 ?
                    thisPlatform.TCSectionIndex[thisPlatform.TCSectionIndex.Count - 1] :
                    thisPlatform.TCSectionIndex[0];
            int beginSectionRouteIndex = ValidRoute[0].GetRouteIndex(beginSectionIndex, 0);

            // check position

            int stationIndex = ValidRoute[0].GetRouteIndex(stationTCSectionIndex, PresentPosition[0].RouteListIndex);

            // if not found from front of train, try from rear of train (front may be beyond platform)
            if (stationIndex < 0)
            {
                stationIndex = ValidRoute[0].GetRouteIndex(stationTCSectionIndex, PresentPosition[1].RouteListIndex);
            }


            // if rear is in platform, station is valid
            if (((((beginSectionRouteIndex != -1 && PresentPosition[1].RouteListIndex == beginSectionRouteIndex) || (PresentPosition[1].RouteListIndex == -1 && PresentPosition[1].TCSectionIndex == beginSectionIndex))
                && PresentPosition[1].TCOffset >= platformBeginOffset) || PresentPosition[1].RouteListIndex > beginSectionRouteIndex) &&
                ((PresentPosition[1].TCSectionIndex == endSectionIndex && PresentPosition[1].TCOffset <= platformEndOffset) || endSectionRouteIndex == -1 || 
                PresentPosition[1].RouteListIndex < endSectionRouteIndex))
            {
                atStation = true;
            }
            // if front is in platform and most of the train is as well, station is valid
            else if (((((endSectionRouteIndex != -1 && PresentPosition[0].RouteListIndex == endSectionRouteIndex) || (PresentPosition[0].RouteListIndex == -1 && PresentPosition[0].TCSectionIndex == endSectionIndex))
                && PresentPosition[0].TCOffset <= platformEndOffset) && ((thisPlatform.Length - (platformEndOffset - PresentPosition[0].TCOffset)) > Length / 2)) || 
                (PresentPosition[0].RouteListIndex != -1 && PresentPosition[0].RouteListIndex < endSectionRouteIndex && 
                (PresentPosition[0].RouteListIndex > beginSectionRouteIndex || (PresentPosition[0].RouteListIndex == beginSectionRouteIndex && PresentPosition[0].TCOffset >= platformBeginOffset))))
            {
                atStation = true;
            }
            // if front is beyond platform and and most of the train is within it, station is valid (isn't it already covered by cases 1 or 4?)
            else if (endSectionRouteIndex != -1 && PresentPosition[0].RouteListIndex == endSectionRouteIndex && PresentPosition[0].TCOffset > platformEndOffset &&
                     (PresentPosition[0].TCOffset - platformEndOffset) < (Length / 3))
            {
                atStation = true;
            }
            // if front is beyond platform and rear is not on route or before platform : train spans platform
            else if (((endSectionRouteIndex != -1 && PresentPosition[0].RouteListIndex > endSectionRouteIndex )|| (endSectionRouteIndex != -1 && PresentPosition[0].RouteListIndex == endSectionRouteIndex && PresentPosition[0].TCOffset >= platformEndOffset))
                  && (PresentPosition[1].RouteListIndex < beginSectionRouteIndex || (PresentPosition[1].RouteListIndex == beginSectionRouteIndex && PresentPosition[1].TCOffset <= platformBeginOffset)))
            {
                atStation = true;
            }

            return atStation;
        }


        //================================================================================================//
        /// <summary>
        /// Update train position
        /// </summary>

        public void UpdateTrainPosition()
        {
            // update positions

            PresentPosition[0].CopyTo(ref PreviousPosition[0]);

            TrackNode tn = FrontTDBTraveller.TN;
            float offset = FrontTDBTraveller.TrackNodeOffset;
            int direction = (int)FrontTDBTraveller.Direction;
            int routeIndex;

            PresentPosition[0].SetTCPosition(tn.TCCrossReference, offset, direction);
            routeIndex = ValidRoute[0].GetRouteIndex(PresentPosition[0].TCSectionIndex, 0);
            PresentPosition[0].RouteListIndex = routeIndex;

            tn = RearTDBTraveller.TN;
            offset = RearTDBTraveller.TrackNodeOffset;
            direction = (int)RearTDBTraveller.Direction;

            PresentPosition[1].SetTCPosition(tn.TCCrossReference, offset, direction);
            routeIndex = ValidRoute[0].GetRouteIndex(PresentPosition[1].TCSectionIndex, 0);
            PresentPosition[1].RouteListIndex = routeIndex;

            if (doJump) // jump do be performed in multiplayer mode when train re-enters game in different position
            {
                doJump = false;
                PresentPosition[0].CopyTo(ref PreviousPosition[0]);
                Trace.TraceInformation("Multiplayer server requested the player train to jump");
                // reset some items
                SignalObjectItems.Clear();
                NextSignalObject[0] = null;
                InitializeSignals(true);
                LastReservedSection[0] = PresentPosition[0].TCSectionIndex;
            }

            // get reserved length
            ReservedTrackLengthM = GetReservedLength();
        }

        //================================================================================================//
        /// <summary>
        /// Update Position linked information
        /// Switches train to Out_Of_Control if it runs out of path
        /// <\summary>

        public void UpdateTrainPositionInformation()
        {

            // check if train still on route - set train to OUT_OF_CONTROL

            PresentPosition[0].DistanceTravelledM = DistanceTravelledM;
            PresentPosition[1].DistanceTravelledM = DistanceTravelledM - Length;

            if (PresentPosition[0].RouteListIndex < 0)
            {
                SetTrainOutOfControl(OUTOFCONTROL.OUT_OF_PATH);
            }
            else if (StationStops.Count > 0)
            {
                StationStop thisStation = StationStops[0];
                thisStation.DistanceToTrainM = ComputeDistanceToNextStation(thisStation);
            }
        }

        //================================================================================================//
        /// <summary>
        /// compute boarding time for activity mode
        /// also check validity of depart time value
        /// <\summary>

        public virtual bool ComputeTrainBoardingTime(StationStop thisStop, ref int stopTime)
        {
            stopTime = thisStop.ComputeStationBoardingTime(this);
            return (thisStop.CheckScheduleValidity(this));
        }

        //================================================================================================//
        /// <summary>
        /// Compute distance to next station
        /// <\summary>
        /// 
        public float ComputeDistanceToNextStation(StationStop thisStation)
        {
            int thisSectionIndex = PresentPosition[0].TCSectionIndex;
            TrackCircuitSection thisSection = signalRef.TrackCircuitList[thisSectionIndex];
            float leftInSectionM = thisSection.Length - PresentPosition[0].TCOffset;
            float distanceToTrainM = -1;
            int stationIndex;

            if (thisStation.SubrouteIndex > TCRoute.activeSubpath && !Simulator.TimetableMode)
            // if the station is in a further subpath, distance computation is longer
            {
                // first compute distance up to end or reverse point of activeSubpath. To be restudied for subpaths with no reversal
                if (TCRoute.ReversalInfo[TCRoute.activeSubpath].Valid)
                    distanceToTrainM = ComputeDistanceToReversalPoint();
                else
                {
                    int lastSectionRouteIndex = TCRoute.TCRouteSubpaths[TCRoute.activeSubpath].Count - 1;
                    float lastSectionLength = signalRef.TrackCircuitList[TCRoute.TCRouteSubpaths[TCRoute.activeSubpath][lastSectionRouteIndex].TCSectionIndex].Length;
                    distanceToTrainM = TCRoute.TCRouteSubpaths[TCRoute.activeSubpath].GetDistanceAlongRoute(PresentPosition[0].RouteListIndex,
                 leftInSectionM, lastSectionRouteIndex, lastSectionLength, true, signalRef);
                }
                float lengthOfIntSubpath = 0;
                int firstSection = 0;
                float firstSectionOffsetToGo = 0;
                int lastSection = 0;
                float lastSectionOffsetToGo = 0;
                int tempSectionTCSectionIndex;
                if (distanceToTrainM >= 0)
                {

                    // compute length of intermediate subpaths, if any, from reversal or section at beginning to reversal or section at end

                    for (int iSubpath = TCRoute.activeSubpath + 1; iSubpath < thisStation.SubrouteIndex; iSubpath++)
                    {
                        if (TCRoute.ReversalInfo[iSubpath - 1].Valid)
                        // skip sections before reversal at beginning of path
                        {
                            for (int iSection = 0; iSection < TCRoute.TCRouteSubpaths[iSubpath].Count; iSection++)
                            {
                                if (TCRoute.TCRouteSubpaths[iSubpath][iSection].TCSectionIndex == TCRoute.ReversalInfo[iSubpath - 1].ReversalSectionIndex)
                                {
                                    firstSection = iSection;
                                    firstSectionOffsetToGo = TCRoute.ReversalInfo[iSubpath - 1].ReverseReversalOffset;
                                    break;
                                }
                            }
                        }
                        else
                        {
                            for (int iSection = 0; iSection < TCRoute.TCRouteSubpaths[iSubpath].Count; iSection++)
                            {
                                if (TCRoute.TCRouteSubpaths[iSubpath][iSection].TCSectionIndex ==
                                    TCRoute.TCRouteSubpaths[iSubpath - 1][TCRoute.TCRouteSubpaths[iSubpath - 1].Count - 1].TCSectionIndex)
                                {
                                    firstSection = iSection + 1;
                                    tempSectionTCSectionIndex = TCRoute.TCRouteSubpaths[iSubpath][firstSection].TCSectionIndex;
                                    firstSectionOffsetToGo = signalRef.TrackCircuitList[tempSectionTCSectionIndex].Length;
                                    break;
                                }
                            }
                        }

                        if (TCRoute.ReversalInfo[iSubpath].Valid)
                        // skip sections before reversal at beginning of path
                        {
                            for (int iSection = TCRoute.TCRouteSubpaths[iSubpath].Count - 1; iSection >= 0; iSection--)
                            {
                                if (TCRoute.TCRouteSubpaths[iSubpath][iSection].TCSectionIndex == TCRoute.ReversalInfo[iSubpath].ReversalSectionIndex)
                                {
                                    lastSection = iSection;
                                    lastSectionOffsetToGo = TCRoute.ReversalInfo[iSubpath].ReverseReversalOffset;
                                    break;
                                }
                            }
                        }
                        else
                        {
                            lastSection = TCRoute.TCRouteSubpaths[iSubpath].Count - 1;
                            tempSectionTCSectionIndex = TCRoute.TCRouteSubpaths[iSubpath][lastSection].TCSectionIndex;
                            lastSectionOffsetToGo = signalRef.TrackCircuitList[tempSectionTCSectionIndex].Length;
                        }

                        lengthOfIntSubpath = TCRoute.TCRouteSubpaths[iSubpath].GetDistanceAlongRoute(firstSection,
                            firstSectionOffsetToGo, lastSection, lastSectionOffsetToGo, true, signalRef);
                        if (lengthOfIntSubpath < 0)
                        {
                            distanceToTrainM = -1;
                            break;
                        }
                        distanceToTrainM += lengthOfIntSubpath;
                    }
                }
                if (distanceToTrainM >= 0)
                {
                    // finally compute distance from start of station subpath up to station
                    if (TCRoute.ReversalInfo[thisStation.SubrouteIndex - 1].Valid)
                    // skip sections before reversal at beginning of path
                    {
                        for (int iSection = 0; iSection < TCRoute.TCRouteSubpaths[thisStation.SubrouteIndex].Count; iSection++)
                        {
                            if (TCRoute.TCRouteSubpaths[thisStation.SubrouteIndex][iSection].TCSectionIndex == TCRoute.ReversalInfo[thisStation.SubrouteIndex - 1].ReversalSectionIndex)
                            {
                                firstSection = iSection;
                                firstSectionOffsetToGo = TCRoute.ReversalInfo[thisStation.SubrouteIndex - 1].ReverseReversalOffset;
                                break;
                            }
                        }
                    }
                    else
                    {
                        for (int iSection = 0; iSection < TCRoute.TCRouteSubpaths[thisStation.SubrouteIndex].Count; iSection++)
                        {
                            if (TCRoute.TCRouteSubpaths[thisStation.SubrouteIndex][iSection].TCSectionIndex ==
                                TCRoute.TCRouteSubpaths[thisStation.SubrouteIndex - 1][TCRoute.TCRouteSubpaths[thisStation.SubrouteIndex - 1].Count - 1].TCSectionIndex)
                            {
                                firstSection = iSection + 1;
                                tempSectionTCSectionIndex = TCRoute.TCRouteSubpaths[thisStation.SubrouteIndex][firstSection].TCSectionIndex;
                                firstSectionOffsetToGo = signalRef.TrackCircuitList[tempSectionTCSectionIndex].Length;
                                break;
                            }
                        }
                    }

                    stationIndex = thisStation.RouteIndex;
                    float distanceFromStartOfsubPath = TCRoute.TCRouteSubpaths[thisStation.SubrouteIndex].GetDistanceAlongRoute(firstSection,
                        firstSectionOffsetToGo, stationIndex, thisStation.StopOffset, true, signalRef);
                    if (distanceFromStartOfsubPath < 0) distanceToTrainM = -1;
                    else distanceToTrainM += distanceFromStartOfsubPath;
                }
            }

            else
            {
                // No enhanced compatibility, simple computation
                // if present position off route, try rear position
                // if both off route, skip station stop
                stationIndex = ValidRoute[0].GetRouteIndex(thisStation.TCSectionIndex, PresentPosition[0].RouteListIndex);
                distanceToTrainM = ValidRoute[0].GetDistanceAlongRoute(PresentPosition[0].RouteListIndex,
                    leftInSectionM, stationIndex, thisStation.StopOffset, true, signalRef);
            }
            return distanceToTrainM;
        }


        //================================================================================================//
        /// <summary>
        /// Compute distance to reversal point
        /// <\summary>

        public float ComputeDistanceToReversalPoint()
        {
            float lengthToGoM = -PresentPosition[0].TCOffset;
            TrackCircuitSection thisSection;
            if (PresentPosition[0].RouteListIndex == -1)
            {
                Trace.TraceWarning("Train {0} service {1} off path; distance to reversal point set to -1", Number, Name);
                return -1;
            }
            // in case the AI train is out of its original path the reversal info is simulated to point to the end of the last route section
            int reversalRouteIndex = ValidRoute[0].Count - 1;
            TrackCircuitSection reversalSection = signalRef.TrackCircuitList[ValidRoute[0][reversalRouteIndex].TCSectionIndex];
            float reverseReversalOffset = reversalSection.Length;
            reversalRouteIndex = ValidRoute[0].GetRouteIndex(TCRoute.ReversalInfo[TCRoute.activeSubpath].ReversalSectionIndex, PresentPosition[0].RouteListIndex);
            if (reversalRouteIndex == -1)
            {
                Trace.TraceWarning("Train {0} service {1}, reversal or end point off path; distance to reversal point set to -1", Number, Name);
                return -1;
            }
            reversalSection = signalRef.TrackCircuitList[TCRoute.ReversalInfo[TCRoute.activeSubpath].ReversalSectionIndex];
            reverseReversalOffset = TCRoute.ReversalInfo[TCRoute.activeSubpath].ReverseReversalOffset;
            if (PresentPosition[0].RouteListIndex <= reversalRouteIndex)
            {
                for (int iElement = PresentPosition[0].RouteListIndex; iElement < ValidRoute[0].Count; iElement++)
                {
                    TCRouteElement thisElement = ValidRoute[0][iElement];
                    thisSection = signalRef.TrackCircuitList[thisElement.TCSectionIndex];
                    if (thisSection.Index == reversalSection.Index)
                    {
                        break;
                    }
                    else lengthToGoM += thisSection.Length;
                }
                return lengthToGoM += reverseReversalOffset;
            }
            else
            {
                for (int iElement = PresentPosition[0].RouteListIndex - 1; iElement >= 0; iElement--)
                {
                    TCRouteElement thisElement = ValidRoute[0][iElement];
                    thisSection = signalRef.TrackCircuitList[thisElement.TCSectionIndex];
                    if (thisSection.Index == reversalSection.Index)
                    {
                        break;
                    }
                    else lengthToGoM -= thisSection.Length;
                }
                return lengthToGoM += reverseReversalOffset - reversalSection.Length;
            }
        }

        //================================================================================================//
        /// <summary>
        /// Compute path length
        /// <\summary>

        public float ComputePathLength()
        {
            float pathLength = 0;
            int tcRouteSubpathIndex = -1;
            foreach (var tcRouteSubpath in TCRoute.TCRouteSubpaths)
            {
                tcRouteSubpathIndex++;
                if (tcRouteSubpathIndex > 0 && TCRoute.ReversalInfo[tcRouteSubpathIndex-1].Valid) pathLength += TCRoute.ReversalInfo[tcRouteSubpathIndex-1].ReverseReversalOffset;
                else if (tcRouteSubpathIndex > 0) pathLength += TCRoute.ReversalInfo[tcRouteSubpathIndex-1].ReverseReversalOffset -
                    signalRef.TrackCircuitList[TCRoute.ReversalInfo[tcRouteSubpathIndex-1].ReversalSectionIndex].Length;
                else { } //start point offset?
                int routeListIndex = 1;
                TrackCircuitSection thisSection;
                int reversalRouteIndex = tcRouteSubpath.GetRouteIndex(TCRoute.ReversalInfo[tcRouteSubpathIndex].ReversalSectionIndex, routeListIndex);
                if (reversalRouteIndex == -1)
                {
                    Trace.TraceWarning("Train {0} service {1}, reversal or end point off path; distance to reversal point set to -1", Number, Name);
                    return -1;
                }
                if (routeListIndex <= reversalRouteIndex)
                {
                    for (int iElement = routeListIndex; iElement < tcRouteSubpath.Count; iElement++)
                    {
                        TCRouteElement thisElement = tcRouteSubpath[iElement];
                        thisSection = signalRef.TrackCircuitList[thisElement.TCSectionIndex];
                        if (thisSection.Index == TCRoute.ReversalInfo[tcRouteSubpathIndex].ReversalSectionIndex)
                        {
                            break;
                        }
                        else pathLength += thisSection.Length;
                    }
                    pathLength += TCRoute.ReversalInfo[tcRouteSubpathIndex].ReverseReversalOffset;
                }
                else
                {
                    pathLength += TCRoute.ReversalInfo[tcRouteSubpathIndex].ReverseReversalOffset -
                    signalRef.TrackCircuitList[TCRoute.ReversalInfo[tcRouteSubpathIndex].ReversalSectionIndex].Length;
                }
            }
            return pathLength;
        }


        //================================================================================================//
        /// <summary>
        /// get list of required actions (only if not moving backward)
        /// </summary>

        public void ObtainRequiredActions(int backward)
        {
            if (this is AITrain && (this as AITrain).MovementState == AITrain.AI_MOVEMENT_STATE.SUSPENDED) return;
            if (backward < backwardThreshold)
            {
                List<DistanceTravelledItem> nowActions = requiredActions.GetActions(DistanceTravelledM);
                if (nowActions.Count > 0)
                {
                    PerformActions(nowActions);
                }
            }
            if (backward < backwardThreshold || SpeedMpS > -0.01)
            {
                List<DistanceTravelledItem> nowActions = AuxActionsContain.specRequiredActions.GetAuxActions(this, DistanceTravelledM);

                if (nowActions.Count > 0)
                {
                    PerformActions(nowActions);
                }
            }
        }

        //================================================================================================//
        /// <summary>
        /// Update section occupy states
        /// Input is backward movement counter
        /// </summary>

        public void UpdateSectionState(int backward)
        {

            List<int[]> sectionList = new List<int[]>();

            int lastIndex = PreviousPosition[0].RouteListIndex;
            int presentIndex = PresentPosition[0].RouteListIndex;

            int lastDTM = Convert.ToInt32(PreviousPosition[0].DistanceTravelledM);
            TrackCircuitSection lastSection = signalRef.TrackCircuitList[PreviousPosition[0].TCSectionIndex];
            int lastDTatEndLastSectionM = lastDTM + Convert.ToInt32(lastSection.Length - PreviousPosition[0].TCOffset);

            int presentDTM = Convert.ToInt32(DistanceTravelledM);

            // don't bother with update if train out of control - all will be reset when train is stopped

            if (ControlMode == TRAIN_CONTROL.OUT_OF_CONTROL)
            {
                return;
            }

            // don't bother with update if train off route - set train to out of control

            if (presentIndex < 0)
            {
                SetTrainOutOfControl(OUTOFCONTROL.OUT_OF_PATH);
                return;
            }

            // train moved backward

            if (backward > backwardThreshold)
            {
                if (presentIndex < lastIndex)
                {
                    int sectionIndex;
                    TrackCircuitSection thisSection;

                    for (int iIndex = lastIndex; iIndex > presentIndex; iIndex--)
                    {
                        sectionIndex = ValidRoute[0][iIndex].TCSectionIndex;
                        sectionList.Add(new int[2] { iIndex, presentDTM });
                        thisSection = signalRef.TrackCircuitList[sectionIndex];
                    }

                    sectionIndex = ValidRoute[0][presentIndex].TCSectionIndex;
                    thisSection = signalRef.TrackCircuitList[sectionIndex];
                    sectionList.Add(new int[2] { presentIndex, presentDTM });
                }
            }

            // train moves forward

            else
            {
                if (presentIndex > lastIndex)
                {
                    int sectionIndex;
                    TrackCircuitSection thisSection;

                    sectionIndex = ValidRoute[0][lastIndex].TCSectionIndex;
                    thisSection = signalRef.TrackCircuitList[sectionIndex];
                    int lastValidDTM = lastDTatEndLastSectionM;

                    for (int iIndex = lastIndex + 1; iIndex < presentIndex; iIndex++)
                    {
                        sectionIndex = ValidRoute[0][iIndex].TCSectionIndex;
                        sectionList.Add(new int[2] { iIndex, lastValidDTM });
                        thisSection = signalRef.TrackCircuitList[sectionIndex];
                        lastValidDTM += Convert.ToInt32(thisSection.Length);
                    }

                    sectionIndex = ValidRoute[0][presentIndex].TCSectionIndex;
                    sectionList.Add(new int[2] { presentIndex, presentDTM });
                }
            }

            // set section states, for AUTOMODE use direction 0 only

            foreach (int[] routeListIndex in sectionList)
            {
                int sectionIndex = ValidRoute[0][routeListIndex[0]].TCSectionIndex;
                TrackCircuitSection thisSection = signalRef.TrackCircuitList[sectionIndex];
                if (!thisSection.CircuitState.ThisTrainOccupying(routedForward))
                {
                    thisSection.SetOccupied(routedForward, routeListIndex[1]);
                    if (!Simulator.TimetableMode && thisSection.CircuitState.HasOtherTrainsOccupying(routedForward))
                    {
                        SwitchToNodeControl(sectionIndex);
                        EndAuthorityType[0] = END_AUTHORITY.TRAIN_AHEAD;
                        ChangeControlModeOtherTrains(thisSection);
                    }
                    // additional actions for child classes
                    UpdateSectionState_Additional(sectionIndex);
                }
            }
        }

        //================================================================================================//
        /// <summary>
        /// Change control mode of other trains in same section if needed
        /// </summary>

        public void ChangeControlModeOtherTrains(TrackCircuitSection thisSection)
        {
            int otherdirection = -1;
            int owndirection = PresentPosition[0].TCDirection;
            foreach (KeyValuePair<TrainRouted, int> trainToCheckInfo in thisSection.CircuitState.TrainOccupy)
            {
                Train OtherTrain = trainToCheckInfo.Key.Train;
                if (OtherTrain.ControlMode == TRAIN_CONTROL.AUTO_SIGNAL) // train is still in signal mode, might need adjusting
                {
                    otherdirection = OtherTrain.PresentPosition[0].TCSectionIndex == thisSection.Index ? OtherTrain.PresentPosition[0].TCDirection :
                        OtherTrain.PresentPosition[1].TCSectionIndex == thisSection.Index ? OtherTrain.PresentPosition[1].TCDirection : -1;
                    if (owndirection >= 0 && otherdirection >= 0) // both trains found
                    {
                        if (owndirection != otherdirection) // opposite directions - this train is now ahead of train in section
                        {
                            OtherTrain.SwitchToNodeControl(thisSection.Index);
                        }
                    }
                }
            }
        }

        //================================================================================================//
        /// <summary>
        /// Check if train went passed signal
        /// if so, and signal was at danger, set train Out_Of_Control
        /// </summary>

        public int CheckSignalPassed(int direction, TCPosition trainPosition, TCPosition trainPreviousPos)
        {
            int passedSignalIndex = -1;
            if (NextSignalObject[direction] != null)
            {

                while (NextSignalObject[direction] != null && !ValidRoute[direction].SignalIsAheadOfTrain(NextSignalObject[direction], trainPosition)) // signal not in front //
                {
                    // correct route index if necessary
                    int correctedRouteIndex = ValidRoute[0].GetRouteIndex(trainPreviousPos.TCSectionIndex, 0);
                    if (correctedRouteIndex >= 0) trainPreviousPos.RouteListIndex = correctedRouteIndex;
                    // check if train really went passed signal in correct direction
                    if (ValidRoute[direction].SignalIsAheadOfTrain(NextSignalObject[direction], trainPreviousPos)) // train was in front on last check, so we did pass
                    {
                        MstsSignalAspect signalState = GetNextSignalAspect(direction);
                        passedSignalIndex = NextSignalObject[direction].thisRef;

#if DEBUG_REPORTS
                        String report = "Passing signal ";
                        report = String.Concat(report, NextSignalObject[direction].thisRef.ToString());
                        report = String.Concat(report, " with state ", signalState.ToString());
                        report = String.Concat(report, " by train ", Number.ToString());
                        report = String.Concat(report, " at ", FormatStrings.FormatDistance(DistanceTravelledM, true));
                        report = String.Concat(report, " and ", FormatStrings.FormatSpeed(SpeedMpS, true));
                        File.AppendAllText(@"C:\temp\printproc.txt", report + "\n");
#endif
#if DEBUG_SIGNALPASS
                        double passtime = 0;
                        if (TrainType != Train.TRAINTYPE.PLAYER)
                        {
                            AITrain aiocctrain = this as AITrain;
                            passtime = aiocctrain.AI.clockTime;
                        }
                        else
                        {
                            passtime = Simulator.ClockTime;
                        }

                        var sob = new StringBuilder();
                        sob.AppendFormat("{0};{1};{2};{3};{4};{5};{6};{7}", Number, Name, NextSignalObject[direction].SignalHeads[0].TDBIndex.ToString(),signalState.ToString(),
                            passtime,DistanceTravelledM,SpeedMpS,Delay);
                        File.AppendAllText(@"C:\temp\passsignal.txt", sob.ToString() + "\n");
#endif

                        if (CheckTrain)
                        {
                            String reportCT = "Passing signal ";
                            reportCT = String.Concat(reportCT, NextSignalObject[direction].thisRef.ToString());
                            reportCT = String.Concat(reportCT, " with state ", signalState.ToString());
                            reportCT = String.Concat(reportCT, " by train ", Number.ToString());
                            reportCT = String.Concat(reportCT, " at ", DistanceTravelledM.ToString());
                            reportCT = String.Concat(reportCT, " and ", FormatStrings.FormatSpeed(SpeedMpS, true));
                            File.AppendAllText(@"C:\temp\checktrain.txt", reportCT + "\n");
                        }

                        if (signalState == MstsSignalAspect.STOP && NextSignalObject[direction].hasPermission == SignalObject.Permission.Denied)
                        {
                            Trace.TraceWarning("Train {1} ({0}) passing signal {2} at {3} at danger at {4}",
                               Number.ToString(), Name, NextSignalObject[direction].thisRef.ToString(),
                               DistanceTravelledM.ToString("###0.0"), SpeedMpS.ToString("##0.00"));
                            SetTrainOutOfControl(OUTOFCONTROL.SPAD);
                            break;
                        }

                        else if (ControlMode == TRAIN_CONTROL.AUTO_SIGNAL && NextSignalObject[direction].sigfound[(int)MstsSignalFunction.NORMAL] < 0) // no next signal
                        {
                            SwitchToNodeControl(LastReservedSection[direction]);
#if DEBUG_REPORTS
                            File.AppendAllText(@"C:\temp\printproc.txt", "Train " + Number.ToString() +
                                            " set to NODE control for no next signal from " + NextSignalObject[direction].thisRef.ToString() + "\n");
#endif
                            if (CheckTrain)
                            {
                                File.AppendAllText(@"C:\temp\checktrain.txt", "Train " + Number.ToString() +
                                                " set to NODE control for no next signal from " + NextSignalObject[direction].thisRef.ToString() + "\n");
                            }
                            break;
                        }
                        else if (ControlMode == TRAIN_CONTROL.AUTO_SIGNAL && NextSignalObject[direction].block_state() != MstsBlockState.CLEAR) // route to next signal not clear
                        {
                            SwitchToNodeControl(LastReservedSection[direction]);
#if DEBUG_REPORTS
                            File.AppendAllText(@"C:\temp\printproc.txt", "Train " + Number.ToString() +
                                            " set to NODE control for route to next signal not clear from " + NextSignalObject[direction].thisRef.ToString() + "\n");
#endif
                            if (CheckTrain)
                            {
                                File.AppendAllText(@"C:\temp\checktrain.txt", "Train " + Number.ToString() +
                                                " set to NODE control for route to next signal not clear from " + NextSignalObject[direction].thisRef.ToString() + "\n");
                            }
                            break;
                        }

                        // get next signal
                        int nextSignalIndex = NextSignalObject[direction].sigfound[(int)MstsSignalFunction.NORMAL];
                        if (nextSignalIndex >= 0)
                        {
                            NextSignalObject[direction] = signalRef.SignalObjects[nextSignalIndex];

                            int reqSectionIndex = NextSignalObject[direction].TCReference;
                            float endOffset = NextSignalObject[direction].TCOffset;

                            DistanceToSignal = GetDistanceToTrain(reqSectionIndex, endOffset);
                        }
                        else
                        {
                            NextSignalObject[direction] = null;
                        }
                    }
                    else
                    {
                        // get next signal
                        int nextSignalIndex = NextSignalObject[direction].sigfound[(int)MstsSignalFunction.NORMAL];
                        if (nextSignalIndex >= 0)
                        {
                            NextSignalObject[direction] = signalRef.SignalObjects[nextSignalIndex];

                            int reqSectionIndex = NextSignalObject[direction].TCReference;
                            float endOffset = NextSignalObject[direction].TCOffset;

                            DistanceToSignal = GetDistanceToTrain(reqSectionIndex, endOffset);
                        }
                        else
                        {
                            NextSignalObject[direction] = null;
                        }
                    }
                }
            }

            return (passedSignalIndex);
        }

        //================================================================================================//
        /// <summary>
        /// Check if train moves backward and if so, check clearance behindtrain
        /// If no save clearance left, set train to Out_Of_Control
        /// </summary>

        public int CheckBackwardClearance()
        {
            bool outOfControl = false;

            int lastIndex = PreviousPosition[0].RouteListIndex;
            float lastOffset = PreviousPosition[0].TCOffset;
            int presentIndex = PresentPosition[0].RouteListIndex;
            float presentOffset = PresentPosition[0].TCOffset;

            if (presentIndex < 0) // we are off the path, stop train //
            {
                SetTrainOutOfControl(OUTOFCONTROL.OUT_OF_PATH);
            }

            // backward

            if (presentIndex < lastIndex || (presentIndex == lastIndex && presentOffset < lastOffset))
            {
                movedBackward = movedBackward < 2 * backwardThreshold ? ++movedBackward : movedBackward;

#if DEBUG_REPORTS
                String report = "Moving backward : ";
                report = String.Concat(report, " train ", Number.ToString());
                File.AppendAllText(@"C:\temp\printproc.txt", report + "\n");
                report = "Previous position : ";
                report = String.Concat(report, lastIndex.ToString(), " + ", lastOffset.ToString());
                File.AppendAllText(@"C:\temp\printproc.txt", report + "\n");
                report = "Present  position : ";
                report = String.Concat(report, presentIndex.ToString(), " + ", presentOffset.ToString());
                File.AppendAllText(@"C:\temp\printproc.txt", report + "\n");
                report = "Backward counter : ";
                report = String.Concat(report, movedBackward.ToString());
                File.AppendAllText(@"C:\temp\printproc.txt", report + "\n");
#endif
                if (CheckTrain)
                {
                    string ctreport = "Moving backward : ";
                    ctreport = String.Concat(ctreport, " train ", Number.ToString());
                    File.AppendAllText(@"C:\temp\checktrain.txt", ctreport + "\n");
                    ctreport = "Previous position : ";
                    ctreport = String.Concat(ctreport, lastIndex.ToString(), " + ", lastOffset.ToString());
                    File.AppendAllText(@"C:\temp\checktrain.txt", ctreport + "\n");
                    ctreport = "Present  position : ";
                    ctreport = String.Concat(ctreport, presentIndex.ToString(), " + ", presentOffset.ToString());
                    File.AppendAllText(@"C:\temp\checktrain.txt", ctreport + "\n");
                    ctreport = "Backward counter : ";
                    ctreport = String.Concat(ctreport, movedBackward.ToString());
                    File.AppendAllText(@"C:\temp\checktrain.txt", ctreport + "\n");
                }
            }

            if (movedBackward > backwardThreshold)
            {
                if (CheckTrain)
                {
                    string ctreport = "Moving backward : exceeding backward threshold : ";
                    ctreport = String.Concat(ctreport, " train ", Number.ToString());
                    File.AppendAllText(@"C:\temp\checktrain.txt", ctreport + "\n");
                }

                // run through sections behind train
                // if still in train route : try to reserve section
                // if multiple train in section : calculate distance to next train, stop oncoming train
                // if section reserved for train : stop train
                // if out of route : set out_of_control
                // if signal : set distance, check if passed

                // TODO : check if other train in section, get distance to train
                // TODO : check correct alignment of any switches passed over while moving backward (reset activepins)

                if (RearSignalObject != null)
                {

                    // create new position some 25 m. behind train as allowed overlap

                    TCPosition overlapPosition = new TCPosition();
                    PresentPosition[1].CopyTo(ref overlapPosition);
                    TrackCircuitSection thisSection = signalRef.TrackCircuitList[overlapPosition.TCSectionIndex];
                    overlapPosition.TCOffset = thisSection.Length - (PresentPosition[1].TCOffset + rearPositionOverlap);  // reverse offset because of reversed direction
                    overlapPosition.TCDirection = overlapPosition.TCDirection == 0 ? 1 : 0; // looking backwards, so reverse direction

                    TrackCircuitSection rearSection = signalRef.TrackCircuitList[RearSignalObject.TCNextTC];
                    if (!TCSubpathRoute.IsAheadOfTrain(rearSection, 0.0f, overlapPosition))
                    {
                        if (RearSignalObject.this_sig_lr(MstsSignalFunction.NORMAL) == MstsSignalAspect.STOP)
                        {
                            Trace.TraceWarning("Train {1} ({0}) passing rear signal {2} at {3} at danger at {4}",
                            Number.ToString(), Name, RearSignalObject.thisRef.ToString(),
                            DistanceTravelledM.ToString("###0.0"), SpeedMpS.ToString("##0.00"));
                            SetTrainOutOfControl(OUTOFCONTROL.SPAD_REAR);
                            outOfControl = true;
                        }
                        else
                        {
                            RearSignalObject = null;   // passed signal, so reset //
                        }
                    }
                }

                if (!outOfControl && RearSignalObject == null)
                {
                    TrackCircuitSection thisSection = signalRef.TrackCircuitList[PresentPosition[1].TCSectionIndex];
                    float clearPath = thisSection.Length - PresentPosition[1].TCOffset;   // looking other direction //
                    int direction = PresentPosition[1].TCDirection == 0 ? 1 : 0;

                    while (clearPath < rearPositionOverlap && !outOfControl && RearSignalObject == null)
                    {
                        if (thisSection.EndSignals[direction] != null)
                        {
                            RearSignalObject = thisSection.EndSignals[direction];
                        }
                        else
                        {
                            int pinLink = direction == 0 ? 1 : 0;

                            // TODO : check required junction and crossover path

                            int nextSectionIndex = thisSection.Pins[pinLink, 0].Link;
                            if (nextSectionIndex >= 0)
                            {
                                TrackCircuitSection nextSection = signalRef.TrackCircuitList[nextSectionIndex];
                                if (!nextSection.IsAvailable(this))
                                {
                                    SetTrainOutOfControl(OUTOFCONTROL.SLIPPED_INTO_PATH);
                                    outOfControl = true;

                                    // stop train in path

                                    List<TrainRouted> trainsInSection = nextSection.CircuitState.TrainsOccupying();
                                    foreach (TrainRouted nextTrain in trainsInSection)
                                    {
                                        nextTrain.Train.ForcedStop(Simulator.Catalog.GetString("Other train is blocking path"), Name, Number);
                                    }

                                    if (nextSection.CircuitState.TrainReserved != null)
                                    {
                                        nextSection.CircuitState.TrainReserved.Train.ForcedStop(Simulator.Catalog.GetString("Other train is blocking path"), Name, Number);
                                    }
                                }
                                else
                                {
                                    clearPath += nextSection.Length;
                                    thisSection = nextSection;
                                    if (thisSection.CircuitType == TrackCircuitSection.TrackCircuitType.EndOfTrack)
                                    {
                                        SetTrainOutOfControl(OUTOFCONTROL.SLIPPED_TO_ENDOFTRACK);
                                        outOfControl = true;
                                    }
                                }
                            }
                        }
                    }

                    if (outOfControl)
                    {
                        ClearanceAtRearM = -1;
                        RearSignalObject = null;
                    }
                    else
                    {
                        ClearanceAtRearM = clearPath;
                    }
                }
            }
            else
            {
                movedBackward = movedBackward >= 0 ? --movedBackward : movedBackward;
                ClearanceAtRearM = -1;
                RearSignalObject = null;
            }

            return (movedBackward);

        }

        //================================================================================================//
        //
        /// <summary>
        // Check for end of route actions - for activity PLAYER train only
        // Reverse train if required
        // Return parameter : true if train still exists (only used in timetable mode)
        /// </summary>
        //

        public virtual bool CheckRouteActions(float elapsedClockSeconds)
        {
            int directionNow = PresentPosition[0].TCDirection;
            int positionNow = PresentPosition[0].TCSectionIndex;
            int directionNowBack = PresentPosition[1].TCDirection;
            int positionNowBack = PresentPosition[1].TCSectionIndex;

            if (PresentPosition[0].RouteListIndex >= 0) directionNow = ValidRoute[0][PresentPosition[0].RouteListIndex].Direction;

            bool[] nextRoute = UpdateRouteActions(elapsedClockSeconds, false);

            AuxActionsContain.SetAuxAction(this);
            if (!nextRoute[0]) return(true);  // not at end of route

            // check if train reversed

            if (nextRoute[1])
            {
                if (positionNowBack == PresentPosition[0].TCSectionIndex && directionNowBack != PresentPosition[0].TCDirection)
                {
                    ReverseFormation(IsActualPlayerTrain);
                    // active subpath must be incremented in parallel in incorporated train if present
                    if (IncorporatedTrainNo >= 0) IncrementSubpath(Simulator.TrainDictionary[IncorporatedTrainNo]);
                }
                else if (positionNow == PresentPosition[1].TCSectionIndex && directionNow != PresentPosition[1].TCDirection)
                {
                    ReverseFormation(IsActualPlayerTrain);
                    // active subpath must be incremented in parallel in incorporated train if present
                    if (IncorporatedTrainNo >= 0) IncrementSubpath(Simulator.TrainDictionary[IncorporatedTrainNo]);
                }
            }

            // check if next station was on previous subpath - if so, move to this subpath

            if (nextRoute[1] && StationStops.Count > 0)
            {
                StationStop thisStation = StationStops[0];
                if (thisStation.SubrouteIndex < TCRoute.activeSubpath)
                {
                    thisStation.SubrouteIndex = TCRoute.activeSubpath;
                }
            }

            return (true); // always return true for activity player train
        }


        //================================================================================================//
        /// <summary>
        /// Check for end of route actions
        /// Called every update, actions depend on route state
        /// returns :
        /// bool[0] "false" end of route not reached
        /// bool[1] "false" if no further route available
        /// </summary>

        public bool[] UpdateRouteActions(float elapsedClockSeconds, bool checkLoop = true)
        {
            bool endOfRoute = false;
            bool[] returnState = new bool[2] { false, false };
            nextRouteReady = false;

            // obtain reversal section index

            int reversalSectionIndex = -1;
            if (TCRoute != null && (ControlMode == TRAIN_CONTROL.AUTO_NODE || ControlMode == TRAIN_CONTROL.AUTO_SIGNAL))
            {
                TCReversalInfo thisReversal = TCRoute.ReversalInfo[TCRoute.activeSubpath];
                if (thisReversal.Valid)
                {
                    reversalSectionIndex = thisReversal.SignalUsed ? thisReversal.LastSignalIndex : thisReversal.LastDivergeIndex;
                }
            }

            // check if train in loop
            // if so, forward to next subroute and continue
            if (checkLoop || StationStops.Count <= 1 || StationStops.Count > 1 && TCRoute != null && StationStops[1].SubrouteIndex > TCRoute.activeSubpath)
            {
                if (TCRoute != null && (ControlMode == TRAIN_CONTROL.AUTO_NODE || ControlMode == TRAIN_CONTROL.AUTO_SIGNAL) && TCRoute.LoopEnd[TCRoute.activeSubpath] >= 0)
                {
                    int loopSectionIndex = ValidRoute[0].GetRouteIndex(TCRoute.LoopEnd[TCRoute.activeSubpath], 0);

                    if (loopSectionIndex >= 0 && PresentPosition[1].RouteListIndex > loopSectionIndex)
                    {
                        int frontSection = TCRoute.TCRouteSubpaths[TCRoute.activeSubpath][PresentPosition[0].RouteListIndex].TCSectionIndex;
                        int rearSection = TCRoute.TCRouteSubpaths[TCRoute.activeSubpath][PresentPosition[1].RouteListIndex].TCSectionIndex;
                        TCRoute.activeSubpath++;
                        ValidRoute[0] = TCRoute.TCRouteSubpaths[TCRoute.activeSubpath];

                        PresentPosition[0].RouteListIndex = ValidRoute[0].GetRouteIndex(frontSection, 0);
                        PresentPosition[1].RouteListIndex = ValidRoute[0].GetRouteIndex(rearSection, 0);

                        // Invalidate preceding section indexes to avoid wrong indexing when building route forward (in Reserve())

                        for (int routeListIndex = 0; routeListIndex < PresentPosition[1].RouteListIndex; routeListIndex++)
                        {
                            ValidRoute[0][routeListIndex].TCSectionIndex = -1;
                        }
                        returnState[0] = true;
                        returnState[1] = true;
                        return (returnState);
                    }

                    // if loopend no longer on this valid route, remove loopend indication
                    else if (loopSectionIndex < 0)
                    {
                        TCRoute.LoopEnd[TCRoute.activeSubpath] = -1;
                    }
                }
            }

            // check position in relation to present end of path

            endOfRoute = CheckEndOfRoutePosition();

            // not end of route - no action

            if (!endOfRoute)
            {
                return (returnState);
            }

            // <CSComment> TODO: check if holding signals correctly released in case of reversal point between WP and signal

            // if next subpath available : check if it can be activated

            bool nextRouteAvailable = false;

            TCSubpathRoute nextRoute = null;

            if (endOfRoute && TCRoute.activeSubpath < (TCRoute.TCRouteSubpaths.Count - 1))
            {
                nextRouteAvailable = true;

                nextRoute = TCRoute.TCRouteSubpaths[TCRoute.activeSubpath + 1];
                int firstSectionIndex = PresentPosition[1].TCSectionIndex;

                // find index of present rear position

                int firstRouteIndex = nextRoute.GetRouteIndex(firstSectionIndex, 0);

                // if not found try index of present front position

                if (firstRouteIndex >= 0)
                {
                    nextRouteReady = true;
                }
                else
                {
                    firstSectionIndex = PresentPosition[0].TCSectionIndex;
                    firstRouteIndex = nextRoute.GetRouteIndex(firstSectionIndex, 0);

                    // cant find next part of route - check if really at end of this route, if so, error, else just wait and see (train stopped for other reason)

                    if (PresentPosition[0].RouteListIndex == ValidRoute[0].Count - 1)
                    {
                        if (firstRouteIndex < 0)
                        {
                            Trace.TraceInformation(
                                "Cannot find next part of route (index {0}) for Train {1} ({2}) (at section {3})",
                                TCRoute.activeSubpath.ToString(), Name, Number.ToString(),
                                PresentPosition[0].TCSectionIndex.ToString());
                        }
                        // search for junction and check if it is not clear

                        else
                        {
                            bool junctionFound = false;
                            bool junctionOccupied = false;

                            for (int iIndex = firstRouteIndex + 1; iIndex < nextRoute.Count && !junctionFound; iIndex++)
                            {
                                int thisSectionIndex = nextRoute[iIndex].TCSectionIndex;
                                TrackCircuitSection thisSection = signalRef.TrackCircuitList[thisSectionIndex];
                                if (thisSection.CircuitType == TrackCircuitSection.TrackCircuitType.Junction)
                                {
                                    junctionFound = true;
                                    if (thisSection.CircuitState.ThisTrainOccupying(this))
                                    {
                                        // Before deciding that route is not yet ready check if the new train head is off path because at end of new route
                                        var thisElement = nextRoute[nextRoute.Count - 1];
                                        thisSection = signalRef.TrackCircuitList[thisElement.TCSectionIndex];
                                        if (thisSection.CircuitState.ThisTrainOccupying(this)) break;
                                        junctionOccupied = true;
                                    }
                                }
                            }

                            if (!junctionOccupied)
                            {
                                nextRouteReady = true;
                            }
                        }
                    }
                    else
                    {
                        endOfRoute = false;
                    }
                }
            }

            // if end reached : clear any remaining reservations ahead

            if (endOfRoute && (!nextRouteAvailable || (nextRouteAvailable && nextRouteReady)))
            {
                if (ControlMode == TRAIN_CONTROL.AUTO_SIGNAL) // for Auto mode try forward only
                {
                    if (NextSignalObject[0] != null && NextSignalObject[0].enabledTrain == routedForward)
                    {
                        NextSignalObject[0].resetSignalEnabled();
                        int nextRouteIndex = ValidRoute[0].GetRouteIndex(NextSignalObject[0].TCNextTC, 0);

                        // clear rest of route to avoid accidental signal activation
                        if (nextRouteIndex >= 0)
                        {
                            signalRef.BreakDownRouteList(ValidRoute[0], nextRouteIndex, routedForward);
                            ValidRoute[0].RemoveRange(nextRouteIndex, ValidRoute[0].Count - nextRouteIndex);
                        }
                    }

                    if (PresentPosition[0].RouteListIndex >= 0 && PresentPosition[0].RouteListIndex < ValidRoute[0].Count - 1) // not at end of route
                    {
                        int nextRouteIndex = PresentPosition[0].RouteListIndex + 1;
                        signalRef.BreakDownRouteList(ValidRoute[0], nextRouteIndex, routedForward);
                        ValidRoute[0].RemoveRange(nextRouteIndex, ValidRoute[0].Count - nextRouteIndex);
                    }
                }

#if DEBUG_REPORTS
                File.AppendAllText(@"C:\temp\printproc.txt",
                                "Train " + Number.ToString() + " at end of path\n");
#endif
                if (CheckTrain)
                {
                    File.AppendAllText(@"C:\temp\checktrain.txt",
                                    "Train " + Number.ToString() + " at end of path\n");
                }

                int nextIndex = PresentPosition[0].RouteListIndex + 1;
                if (nextIndex <= (ValidRoute[0].Count - 1))
                {
                    signalRef.BreakDownRoute(ValidRoute[0][nextIndex].TCSectionIndex, routedForward);
                }

                // clear any remaining deadlocks
                ClearDeadlocks();
                DeadlockInfo.Clear();
            }

            // if next route available : reverse train, reset and reinitiate signals

            if (endOfRoute && nextRouteAvailable && nextRouteReady)
            {

                // check if reverse is required

                int newIndex = nextRoute.GetRouteIndex(PresentPosition[0].TCSectionIndex, 0);
                var oldDirection = ValidRoute[0][PresentPosition[0].RouteListIndex].Direction;
                if (newIndex < 0)
                {
                    newIndex = nextRoute.GetRouteIndex(PresentPosition[1].TCSectionIndex, 0);
                    oldDirection = ValidRoute[0][PresentPosition[1].RouteListIndex].Direction;
                }

                if (oldDirection != nextRoute[newIndex].Direction)
                {

                    // set new train positions and reset distance travelled

                    TCPosition tempPosition = new TCPosition();
                    PresentPosition[0].CopyTo(ref tempPosition);
                    PresentPosition[1].CopyTo(ref PresentPosition[0]);
                    tempPosition.CopyTo(ref PresentPosition[1]);

                    PresentPosition[0].Reverse(ValidRoute[0][PresentPosition[0].RouteListIndex].Direction, nextRoute, Length, signalRef);
                    PresentPosition[0].CopyTo(ref PreviousPosition[0]);
                    PresentPosition[1].Reverse(ValidRoute[0][PresentPosition[1].RouteListIndex].Direction, nextRoute, 0.0f, signalRef);
                }
                else
                {
                    PresentPosition[0].RouteListIndex = nextRoute.GetRouteIndex(PresentPosition[0].TCSectionIndex, 0);
                    PresentPosition[1].RouteListIndex = nextRoute.GetRouteIndex(PresentPosition[1].TCSectionIndex, 0);
                    PresentPosition[0].CopyTo(ref PreviousPosition[0]);
                }

                DistanceTravelledM = PresentPosition[0].DistanceTravelledM;

                // perform any remaining actions of type clear section (except sections now occupied)

#if DEBUG_REPORTS
                int nextSubpath = TCRoute.activeSubpath + 1;
                File.AppendAllText(@"C:\temp\printproc.txt",
                                "Train " + Number.ToString() +
                                " starts subpath " + nextSubpath.ToString() + "\n");
#endif
                if (CheckTrain)
                {
                    int nextSubpathCT = TCRoute.activeSubpath + 1;
                    File.AppendAllText(@"C:\temp\checktrain.txt",
                                    "Train " + Number.ToString() +
                                    " starts subpath " + nextSubpathCT.ToString() + "\n");
                }

                // reset old actions
                ClearActiveSectionItems();

                if (CheckTrain)
                {
                    File.AppendAllText(@"C:\temp\checktrain.txt", "* Remaining active items : \n");
                    LinkedListNode<DistanceTravelledItem> nextNode = requiredActions.First;
                    while (nextNode != null)
                    {
                        File.AppendAllText(@"C:\temp\checktrain.txt",
                            " -- Distance : " + nextNode.Value.RequiredDistance + " ; Type : " + nextNode.Value.GetType().ToString() + "\n");
                        nextNode = nextNode.Next;
                    }
                }

                // set new route
                TCRoute.activeSubpath++;
                ValidRoute[0] = TCRoute.TCRouteSubpaths[TCRoute.activeSubpath];


                TCRoute.SetReversalOffset(Length, Simulator.TimetableMode);

                // clear existing list of occupied track, and build new list
                for (int iSection = OccupiedTrack.Count - 1; iSection >= 0; iSection--)
                {
                    TrackCircuitSection thisSection = OccupiedTrack[iSection];
                    thisSection.ResetOccupied(this);

                }
                int rearIndex = PresentPosition[1].RouteListIndex;

                if (rearIndex < 0) // end of train not on new route
                {
                    TCSubpathRoute tempRoute = signalRef.BuildTempRoute(this, PresentPosition[1].TCSectionIndex, PresentPosition[1].TCOffset,
                        PresentPosition[1].TCDirection, Length, false, true, false);

                    for (int iIndex = 0; iIndex < tempRoute.Count; iIndex++)
                    {
                        TrackCircuitSection thisSection = signalRef.TrackCircuitList[tempRoute[iIndex].TCSectionIndex];
                        thisSection.SetOccupied(routedForward);
                    }
                }
                else
                {
                    for (int iIndex = PresentPosition[1].RouteListIndex; iIndex <= PresentPosition[0].RouteListIndex; iIndex++)
                    {
                        TrackCircuitSection thisSection = signalRef.TrackCircuitList[ValidRoute[0][iIndex].TCSectionIndex];
                        thisSection.SetOccupied(routedForward);
                    }
                }

                // Check deadlock against all other trains
                CheckDeadlock(ValidRoute[0], Number);

                // reset signal information

                SignalObjectItems.Clear();
                NextSignalObject[0] = null;

                InitializeSignals(true);

                LastReservedSection[0] = PresentPosition[0].TCSectionIndex;

                // clear claims of any trains which have claimed present occupied sections upto common point - this avoids deadlocks
                // trains may have claimed while train was reversing

                TrackCircuitSection presentSection = signalRef.TrackCircuitList[LastReservedSection[0]];
                presentSection.ClearReversalClaims(routedForward);

                // switch to NODE mode
                if (ControlMode == TRAIN_CONTROL.AUTO_SIGNAL)
                {
                    SwitchToNodeControl(PresentPosition[0].TCSectionIndex);
                }
            }

            returnState[0] = endOfRoute;
            returnState[1] = nextRouteAvailable;

            return (returnState);  // return state
        }

        //================================================================================================//
        /// <summary>
        /// Check End of Route Position
        /// </summary>

        public virtual bool CheckEndOfRoutePosition()
        {
            bool endOfRoute = false;

            // obtain reversal section index

            int reversalSectionIndex = -1;
            if (TCRoute != null && (ControlMode == TRAIN_CONTROL.AUTO_NODE || ControlMode == TRAIN_CONTROL.AUTO_SIGNAL))
            {
                TCReversalInfo thisReversal = TCRoute.ReversalInfo[TCRoute.activeSubpath];
                if (thisReversal.Valid)
                {
                    reversalSectionIndex = thisReversal.SignalUsed ? thisReversal.LastSignalIndex : thisReversal.LastDivergeIndex;
                }
            }

            // check if present subroute ends in reversal or is last subroute
            if (TCRoute.ReversalInfo[TCRoute.activeSubpath].Valid || TCRoute.activeSubpath == TCRoute.TCRouteSubpaths.Count - 1)
            {
                // can only be performed if train is stationary

                if (Math.Abs(SpeedMpS) > 0.03)
                    return (endOfRoute);

                // check position in relation to present end of path
                // front is in last route section
                if (PresentPosition[0].RouteListIndex == (ValidRoute[0].Count - 1) &&
                    (!TCRoute.ReversalInfo[TCRoute.activeSubpath].Valid && TCRoute.activeSubpath < TCRoute.TCRouteSubpaths.Count - 1))
                {
                    endOfRoute = true;
                }
                // front is within 150m. of end of route and no junctions inbetween (only very short sections ahead of train)
                else
                {
                    TrackCircuitSection thisSection = signalRef.TrackCircuitList[PresentPosition[0].TCSectionIndex];
                    float lengthToGo = thisSection.Length - PresentPosition[0].TCOffset;

                    bool junctionFound = false;
                    if (TCRoute.activeSubpath < TCRoute.TCRouteSubpaths.Count - 1)
                    {
                        for (int iIndex = PresentPosition[0].RouteListIndex + 1; iIndex < ValidRoute[0].Count && !junctionFound; iIndex++)
                        {
                            thisSection = signalRef.TrackCircuitList[ValidRoute[0][iIndex].TCSectionIndex];
                            junctionFound = thisSection.CircuitType == TrackCircuitSection.TrackCircuitType.Junction;
                            lengthToGo += thisSection.Length;
                        }
                    }
                    else lengthToGo = ComputeDistanceToReversalPoint();
                    float compatibilityNegligibleRouteChunk = ((TrainType == TRAINTYPE.AI || TrainType == TRAINTYPE.AI_PLAYERHOSTING)
                        && TCRoute.TCRouteSubpaths.Count - 1 == TCRoute.activeSubpath) ? 40f : 5f;
                    float negligibleRouteChunk = compatibilityNegligibleRouteChunk;

                    if (lengthToGo < negligibleRouteChunk && !junctionFound && !TCRoute.ReversalInfo[TCRoute.activeSubpath].Valid)
                    {
                        endOfRoute = true;
                    }
                }
                
                //<CSComment: check of vicinity to reverse point; only in subpaths ending with reversal
                if (TCRoute.ReversalInfo[TCRoute.activeSubpath].Valid)
                {
                    float distanceToReversalPoint = ComputeDistanceToReversalPoint();
                    if (distanceToReversalPoint < 50 && PresentPosition[1].RouteListIndex >= reversalSectionIndex)
                        endOfRoute = true;
                }
                // other checks unrelated to state
                if (!endOfRoute)
                {
                    // if last entry in route is END_OF_TRACK, check against previous entry as this can never be the trains position nor a signal reference section
                    int lastValidRouteIndex = ValidRoute[0].Count - 1;
                    if (signalRef.TrackCircuitList[ValidRoute[0][lastValidRouteIndex].TCSectionIndex].CircuitType == TrackCircuitSection.TrackCircuitType.EndOfTrack)
                        lastValidRouteIndex--;

                    // if waiting for next signal and section beyond signal is last in route and there is no valid reversal index - end of route reached
                    if (NextSignalObject[0] != null && PresentPosition[0].TCSectionIndex == NextSignalObject[0].TCReference &&
                         NextSignalObject[0].TCNextTC == ValidRoute[0][lastValidRouteIndex].TCSectionIndex && reversalSectionIndex < 0 &&
                         NextSignalObject[0].this_sig_lr(MstsSignalFunction.NORMAL) == MstsSignalAspect.STOP && TCRoute.ReversalInfo[TCRoute.activeSubpath].Valid)
                    {
                        endOfRoute = true;
                    }
                }
            }

            // MSTS double reversal point: can be recognized and passed at speed > 0
            else
            {
                var distanceToReversalPoint = ComputeDistanceToReversalPoint();
                if (distanceToReversalPoint <= 0 && distanceToReversalPoint != -1) endOfRoute = true;
            }

            return (endOfRoute);
        }

        //================================================================================================//
        /// <summary>
        /// Update route clearance ahead of train
        /// Called every update, actions depend on present control state
        /// </summary>

        public void UpdateRouteClearanceAhead(int signalObjectIndex, int backward, float elapsedClockSeconds)
        {
            switch (ControlMode)
            {
                case (TRAIN_CONTROL.AUTO_SIGNAL):
                    {
                        UpdateSignalMode(signalObjectIndex, backward, elapsedClockSeconds);
                        break;
                    }
                case (TRAIN_CONTROL.AUTO_NODE):
                    {
                        UpdateNodeMode();
                        break;
                    }
                case (TRAIN_CONTROL.OUT_OF_CONTROL):
                    {
                        UpdateOutOfControl();
                        if (LeadLocomotive != null)
                            ((MSTSLocomotive)LeadLocomotive).SetEmergency(true);
                        break;
                    }
                case (TRAIN_CONTROL.UNDEFINED):
                    {
                        SwitchToNodeControl(-1);
                        break;
                    }

                // other modes are processed directly
                default:
                    break;
            }

            // reset signal which we've just passed

            if (signalObjectIndex >= 0)
            {
                var signalObject = signalRef.SignalObjects[signalObjectIndex];

                //the following is added by JTang, passing a hold signal, will take back control by the system
                if (signalObject.holdState == SignalObject.HoldState.ManualPass ||
                    signalObject.holdState == SignalObject.HoldState.ManualApproach)
                {
                    signalObject.holdState = SignalObject.HoldState.None;
                }

                signalObject.resetSignalEnabled();
            }
        }

        //================================================================================================//
        /// <summary>
        /// Perform auto signal mode update
        /// </summary>

        public void UpdateSignalMode(int signalObjectIndex, int backward, float elapsedClockSeconds)
        {
            // in AUTO mode, use forward route only
            // if moving backward, check if slipped passed signal, if so, re-enable signal

            if (backward > backwardThreshold)
            {
                if (NextSignalObject[0] != null && NextSignalObject[0].enabledTrain != routedForward)
                {
                    if (NextSignalObject[0].enabledTrain != null)
                    {
                        NextSignalObject[0].ResetSignal(true);
                    }
                    signalObjectIndex = NextSignalObject[0].thisRef;
                }
            }

            // if signal passed, send request to clear to next signal
            // if next signal not enabled, also send request (can happen after choosing passing path)

            if (signalObjectIndex >= 0)
            {
                var thisSignal = signalRef.SignalObjects[signalObjectIndex];
                int nextSignalIndex = thisSignal.sigfound[(int)MstsSignalFunction.NORMAL];
                if (nextSignalIndex >= 0)
                {
                    var nextSignal = signalRef.SignalObjects[nextSignalIndex];
                    nextSignal.requestClearSignal(ValidRoute[0], routedForward, 0, false, null);
                }
            }

            // if next signal not enabled or enabled for other train, also send request (can happen after choosing passing path or after detach)

            else if (NextSignalObject[0] != null && (!NextSignalObject[0].enabled || NextSignalObject[0].enabledTrain != routedForward))
            {
                NextSignalObject[0].requestClearSignal(ValidRoute[0], routedForward, 0, false, null);
            }


            // check if waiting for signal

            else if (SpeedMpS < Math.Abs(0.1) &&
             NextSignalObject[0] != null &&
                     GetNextSignalAspect(0) == MstsSignalAspect.STOP &&
                     CheckTrainWaitingForSignal(NextSignalObject[0], 0))
            {
                bool hasClaimed = ClaimState;
                bool claimAllowed = true;

                // perform special actions on stopped at signal for specific train classes
                ActionsForSignalStop(ref claimAllowed);

                // cannot claim on deadlock to prevent further deadlocks
                bool DeadlockWait = CheckDeadlockWait(NextSignalObject[0]);
                if (DeadlockWait) claimAllowed = false;

                // cannot claim while in waitstate as this would lock path for other train
                if (isInWaitState()) claimAllowed = false;

                // cannot claim on hold signal
                if (HoldingSignals.Contains(NextSignalObject[0].thisRef)) claimAllowed = false;

                // process claim if allowed
                if (claimAllowed)
                {
                    if (CheckStoppedTrains(NextSignalObject[0].signalRoute)) // do not claim when train ahead is stationary or in Manual mode
                    {
                        actualWaitTimeS = standardWaitTimeS;  // allow immediate claim if other train moves
                        ClaimState = false;
                    }
                    else
                    {
                        actualWaitTimeS += elapsedClockSeconds;
                        if (actualWaitTimeS > standardWaitTimeS)
                        {
                            ClaimState = true;
                        }
                    }
                }
                else
                {
                    actualWaitTimeS = 0.0f;
                    ClaimState = false;

                    // Reset any invalid claims (occurs on WAIT commands, reason still to be checked!) - not unclaiming causes deadlocks
                    for (int iIndex = PresentPosition[0].RouteListIndex; iIndex <= ValidRoute[0].Count - 1; iIndex++)
                    {
                        int sectionIndex = ValidRoute[0][iIndex].TCSectionIndex;
                        TrackCircuitSection claimSection = signalRef.TrackCircuitList[sectionIndex];
                        if (claimSection.CircuitState.TrainClaimed.ContainsTrain(routedForward))
                        {
                            claimSection.UnclaimTrain(routedForward);
                        }
                    }
                }
            }
            else
            {
                actualWaitTimeS = 0.0f;
                ClaimState = false;
            }
        }

        //================================================================================================//
        //
        // Check if train is waiting for a stationary (stopped) train or a train in manual mode
        //

        public bool CheckStoppedTrains(TCSubpathRoute thisRoute)
        {
            foreach (TCRouteElement thisElement in thisRoute)
            {
                TrackCircuitSection thisSection = signalRef.TrackCircuitList[thisElement.TCSectionIndex];
                foreach (KeyValuePair<TrainRouted, int> thisTrain in thisSection.CircuitState.TrainOccupy)
                {
                    if (thisTrain.Key.Train.SpeedMpS == 0.0f)
                    {
                        return (true);
                    }
                    if (thisTrain.Key.Train.ControlMode == TRAIN_CONTROL.MANUAL)
                    {
                        return (true);
                    }
                }
            }

            return (false);
        }

        //================================================================================================//
        /// <summary>
        /// Test if call on allowed
        /// </summary>
        /// <param name="thisSignal"></param>
        /// <param name="allowOnNonePlatform"></param>
        /// <param name="thisRoute"></param>
        /// <param name="dumpfile"></param>
        /// <returns></returns>
        /// 

        public virtual bool TestCallOn(SignalObject thisSignal, bool allowOnNonePlatform, TCSubpathRoute thisRoute, string dumpfile)
        {
            bool intoPlatform = false;

            foreach (Train.TCRouteElement routeElement in thisSignal.signalRoute)
            {
                TrackCircuitSection routeSection = signalRef.TrackCircuitList[routeElement.TCSectionIndex];

                // check if route leads into platform

                if (routeSection.PlatformIndex.Count > 0)
                {
                    intoPlatform = true;
                }
            }

            if (!intoPlatform)
            {
                //if track does not lead into platform, return state as defined in call
                if (!String.IsNullOrEmpty(dumpfile))
                {
                    var sob = new StringBuilder();
                    sob.AppendFormat("CALL ON : Train {0} : {1} - route does not lead into platform \n", Name, allowOnNonePlatform);
                    File.AppendAllText(dumpfile, sob.ToString());
                }
                return (allowOnNonePlatform);
            }
            else
            {
                // never allow if track leads into platform
                if (!String.IsNullOrEmpty(dumpfile))
                {
                    var sob = new StringBuilder();
                    sob.AppendFormat("CALL ON : Train {0} : invalid - route leads into platform \n", Name);
                    File.AppendAllText(dumpfile, sob.ToString());
                }
                return (false);
            }
        }

        //================================================================================================//
        /// <summary>
        /// Check if train is waiting for signal
        /// </summary>

        public bool CheckTrainWaitingForSignal(SignalObject thisSignal, int direction)
        {
            TrainRouted thisRouted = direction == 0 ? routedForward : routedBackward;
            int trainRouteIndex = PresentPosition[direction].RouteListIndex;
            int signalRouteIndex = ValidRoute[direction].GetRouteIndex(thisSignal.TCReference, trainRouteIndex);

            // signal section is not in train route, so train can't be waiting for signal

            if (signalRouteIndex < 0)
            {
                return (false);
            }

            // check if any other trains in section ahead of this train

            int thisSectionIndex = ValidRoute[0][trainRouteIndex].TCSectionIndex;
            TrackCircuitSection thisSection = signalRef.TrackCircuitList[thisSectionIndex];

            Dictionary<Train, float> trainAhead = thisSection.TestTrainAhead(this,
                    PresentPosition[0].TCOffset, PresentPosition[0].TCDirection);

            if (trainAhead.Count > 0)
            {
                KeyValuePair<Train, float> foundTrain = trainAhead.ElementAt(0);
                // check if train is closer as signal
                if (!DistanceToSignal.HasValue || foundTrain.Value < DistanceToSignal)
                {
                    return (false);
                }
            }

            // check if any other sections inbetween train and signal

            if (trainRouteIndex != signalRouteIndex)
            {
                for (int iIndex = trainRouteIndex + 1; iIndex <= signalRouteIndex; iIndex++)
                {
                    int nextSectionIndex = ValidRoute[0][iIndex].TCSectionIndex;
                    TrackCircuitSection nextSection = signalRef.TrackCircuitList[nextSectionIndex];

                    if (nextSection.CircuitState.HasTrainsOccupying())  // train is ahead - it's not our signal //
                    {
                        return (false);
                    }
                    else if (!nextSection.IsAvailable(this)) // is section really available to us? //

                    // something is wrong - section upto signal is not available - give warning and switch to node control
                    // also reset signal if it was enabled to us
                    {
                        Trace.TraceWarning("Train {0} ({1}) in Signal control but route to signal not cleared - switching to Node control",
                                Name, Number);

                        if (thisSignal.enabledTrain == thisRouted)
                        {
                            thisSignal.ResetSignal(true);
                        }
                        SwitchToNodeControl(thisSection.Index);

                        return (false);
                    }
                }
            }

            // we are waiting, but is signal clearance requested ?

            if (thisSignal.enabledTrain == null)
            {
                thisSignal.requestClearSignal(ValidRoute[0], thisRouted, 0, false, null);
            }

            // we are waiting, but is it really our signal ?

            else if (thisSignal.enabledTrain != thisRouted)
            {

                // something is wrong - we are waiting, but it is not our signal - give warning, reset signal and clear route

                Trace.TraceWarning("Train {0} ({1}) waiting for signal which is enabled to train {2}",
                        Name, Number, thisSignal.enabledTrain.Train.Number);

                // stop other train - switch other train to node control

                Train otherTrain = thisSignal.enabledTrain.Train;
                otherTrain.LastReservedSection[0] = -1;
                if (Math.Abs(otherTrain.SpeedMpS) > 0)
                {
                    otherTrain.ForcedStop(Simulator.Catalog.GetString("Stopped due to errors in route setting"), Name, Number);
                }
                otherTrain.SwitchToNodeControl(-1);

                // reset signal and clear route

                thisSignal.ResetSignal(false);
                thisSignal.requestClearSignal(ValidRoute[0], thisRouted, 0, false, null);
                return (false);   // do not yet set to waiting, signal might clear //
            }

            // signal is in holding list - so not really waiting - but remove from list if held for station stop

            if (thisSignal.holdState == SignalObject.HoldState.ManualLock)
            {
                return (false);
            }
            else if (thisSignal.holdState == SignalObject.HoldState.StationStop && HoldingSignals.Contains(thisSignal.thisRef))
            {
                if (StationStops != null && StationStops.Count > 0 && StationStops[0].ExitSignal != thisSignal.thisRef) // not present station stop
                {
                    HoldingSignals.Remove(thisSignal.thisRef);
                    thisSignal.holdState = SignalObject.HoldState.None;
                    return (false);
                }
            }

            return (true);  // it is our signal and we are waiting //
        }

        //================================================================================================//
        /// <summary>
        /// Breakdown claimed route when signal set to hold
        /// </summary>

        public void BreakdownClaim(TrackCircuitSection thisSection, int routeDirectionIndex, TrainRouted thisTrainRouted)
        {
            TrackCircuitSection nextSection = thisSection;
            int routeIndex = ValidRoute[routeDirectionIndex].GetRouteIndex(thisSection.Index, PresentPosition[routeDirectionIndex].RouteListIndex);
            bool isClaimed = thisSection.CircuitState.TrainClaimed.Contains(thisTrainRouted);

            for (int iIndex = routeIndex + 1; iIndex < (ValidRoute[routeDirectionIndex].Count - 1) && isClaimed; iIndex++)
            {
                thisSection.RemoveTrain(this, false);
                nextSection = signalRef.TrackCircuitList[ValidRoute[routeDirectionIndex][iIndex].TCSectionIndex];
            }
        }


        //================================================================================================//
        /// <summary>
        /// Perform auto node mode update
        /// </summary>

        public virtual void UpdateNodeMode()
        {

            // update distance to end of authority

            int lastRouteIndex = ValidRoute[0].GetRouteIndex(LastReservedSection[0], PresentPosition[0].RouteListIndex);

            TrackCircuitSection thisSection = signalRef.TrackCircuitList[PresentPosition[0].TCSectionIndex];
            DistanceToEndNodeAuthorityM[0] = thisSection.Length - PresentPosition[0].TCOffset;

            for (int iSection = PresentPosition[0].RouteListIndex + 1; iSection <= lastRouteIndex; iSection++)
            {
                thisSection = signalRef.TrackCircuitList[ValidRoute[0][iSection].TCSectionIndex];
                DistanceToEndNodeAuthorityM[0] += thisSection.Length;
            }


            // run out of authority : train is out of control

            // TODO : check end of (sub)path
            //        set variable accordingly
            //
            //            if (DistanceToEndNodeAuthorityM < 0.0f)
            //            {
            //                SetTrainOutOfControl(OUTOFCONTROL.OUT_OF_AUTHORITY);
            //                return;
            //            }

            // look maxTimeS or minCheckDistance ahead
            float maxDistance = Math.Max(AllowedMaxSpeedMpS * maxTimeS, minCheckDistanceM);
            if (EndAuthorityType[0] == END_AUTHORITY.MAX_DISTANCE && DistanceToEndNodeAuthorityM[0] > maxDistance)
            {
                return;   // no update required //
            }

            // perform node update - forward only

            signalRef.requestClearNode(routedForward, ValidRoute[0]);
        }

        //================================================================================================//
        /// <summary>
        /// Switches switch after dispatcher window command, when in auto mode
        /// </summary>

        public bool ProcessRequestAutoSetSwitch(int reqSwitchIndex)
        {
            TrackCircuitSection reqSwitch = signalRef.TrackCircuitList[reqSwitchIndex];

            bool switchSet = false;
            if (reqSwitch.CircuitState.TrainReserved != null && reqSwitch.CircuitState.TrainReserved.Train == this)
            {
                // store required position
                int reqSwitchPosition = reqSwitch.JunctionSetManual;
                ClearReservedSections();
                Reinitialize();
                reqSwitch.JunctionSetManual = reqSwitchPosition;
            }
            switchSet = true;
            return switchSet;
        }

        //================================================================================================//
        /// <summary>
        /// Update section occupy states for manual mode
        /// Note : manual mode has no distance actions so sections must be cleared immediately
        /// </summary>

        public void UpdateSectionStateManual()
        {
            // occupation is set in forward mode only
            // build route from rear to front - before reset occupy so correct switch alignment is used
            TrainRoute = signalRef.BuildTempRoute(this, PresentPosition[1].TCSectionIndex, PresentPosition[1].TCOffset, PresentPosition[1].TCDirection, Length, false, true, false);

            // save present occupation list

            List<TrackCircuitSection> clearedSections = new List<TrackCircuitSection>();
            for (int iindex = OccupiedTrack.Count - 1; iindex >= 0; iindex--)
            {
                clearedSections.Add(OccupiedTrack[iindex]);
            }

            // set track occupied

            OccupiedTrack.Clear();

            foreach (TCRouteElement thisElement in TrainRoute)
            {
                TrackCircuitSection thisSection = signalRef.TrackCircuitList[thisElement.TCSectionIndex];

                if (clearedSections.Contains(thisSection))
                {
                    thisSection.ResetOccupied(this); // reset occupation if it was occupied
                    clearedSections.Remove(thisSection);  // remove from cleared list
                }

                thisSection.Reserve(routedForward, TrainRoute);  // reserve first to reset switch alignments
                thisSection.SetOccupied(routedForward);
            }

            foreach (TrackCircuitSection exSection in clearedSections)
            {
                exSection.ClearOccupied(this, true); // sections really cleared
            }
        }

        //================================================================================================//
        /// <summary>
        /// Update Manual Mode
        /// </summary>

        public void UpdateManualMode(int signalObjectIndex)
        {
            // check present forward
            TCSubpathRoute newRouteF = CheckManualPath(0, PresentPosition[0], ValidRoute[0], true, ref EndAuthorityType[0],
                ref DistanceToEndNodeAuthorityM[0]);
            ValidRoute[0] = newRouteF;
            int routeIndex = ValidRoute[0].GetRouteIndex(PresentPosition[0].TCSectionIndex, 0);
            PresentPosition[0].RouteListIndex = routeIndex;

            // check present reverse
            // reverse present rear position direction to build correct path backwards
            TCPosition tempRear = new TCPosition();
            PresentPosition[1].CopyTo(ref tempRear);
            tempRear.TCDirection = tempRear.TCDirection == 0 ? 1 : 0;
            TCSubpathRoute newRouteR = CheckManualPath(1, tempRear, ValidRoute[1], true, ref EndAuthorityType[1],
                ref DistanceToEndNodeAuthorityM[1]);
            ValidRoute[1] = newRouteR;


            // select valid route

            if (MUDirection == Direction.Forward)
            {
                // use position from other end of section
                float reverseOffset = signalRef.TrackCircuitList[PresentPosition[1].TCSectionIndex].Length - PresentPosition[1].TCOffset;
                CheckSpeedLimitManual(ValidRoute[1], TrainRoute, reverseOffset, PresentPosition[1].TCOffset, signalObjectIndex, 0);
            }
            else
            {
                TCSubpathRoute tempRoute = new TCSubpathRoute(); // reversed trainRoute
                for (int iindex = TrainRoute.Count - 1; iindex >= 0; iindex--)
                {
                    TCRouteElement thisElement = TrainRoute[iindex];
                    thisElement.Direction = thisElement.Direction == 0 ? 1 : 0;
                    tempRoute.Add(thisElement);
                }
                float reverseOffset = signalRef.TrackCircuitList[PresentPosition[0].TCSectionIndex].Length - PresentPosition[0].TCOffset;
                CheckSpeedLimitManual(ValidRoute[0], tempRoute, PresentPosition[0].TCOffset, reverseOffset, signalObjectIndex, 1);
            }

            // reset signal

            if (signalObjectIndex >= 0)
            {
                var thisSignal = signalRef.SignalObjects[signalObjectIndex];
                thisSignal.hasPermission = SignalObject.Permission.Denied;
                //the following is added by JTang, passing a hold signal, will take back control by the system
                if (thisSignal.holdState == SignalObject.HoldState.ManualPass ||
                    thisSignal.holdState == SignalObject.HoldState.ManualApproach) thisSignal.holdState = SignalObject.HoldState.None;

                thisSignal.resetSignalEnabled();
            }

            // get next signal

            // forward
            NextSignalObject[0] = null;
            for (int iindex = 0; iindex < ValidRoute[0].Count && NextSignalObject[0] == null; iindex++)
            {
                TCRouteElement thisElement = ValidRoute[0][iindex];
                TrackCircuitSection thisSection = signalRef.TrackCircuitList[thisElement.TCSectionIndex];
                NextSignalObject[0] = thisSection.EndSignals[thisElement.Direction];
            }

            // backward
            NextSignalObject[1] = null;
            for (int iindex = 0; iindex < ValidRoute[1].Count && NextSignalObject[1] == null; iindex++)
            {
                TCRouteElement thisElement = ValidRoute[1][iindex];
                TrackCircuitSection thisSection = signalRef.TrackCircuitList[thisElement.TCSectionIndex];
                NextSignalObject[1] = thisSection.EndSignals[thisElement.Direction];
            }

            // clear all build up distance actions
            requiredActions.RemovePendingAIActionItems(true);
        }


        //================================================================================================//
        /// <summary>
        /// Check Manual Path
        /// <\summary>

        public TCSubpathRoute CheckManualPath(int direction, TCPosition requiredPosition, TCSubpathRoute requiredRoute, bool forward,
            ref END_AUTHORITY endAuthority, ref float endAuthorityDistanceM)
        {
            TrainRouted thisRouted = direction == 0 ? routedForward : routedBackward;

            // create new route or set to existing route

            TCSubpathRoute newRoute = null;

            TCRouteElement thisElement = null;
            TrackCircuitSection thisSection = null;
            int reqDirection = 0;
            float offsetM = 0.0f;
            float totalLengthM = 0.0f;

            if (requiredRoute == null)
            {
                newRoute = new TCSubpathRoute();
            }
            else
            {
                newRoute = requiredRoute;
            }

            // check if train on valid position in route

            int thisRouteIndex = newRoute.GetRouteIndex(requiredPosition.TCSectionIndex, 0);
            if (thisRouteIndex < 0)    // no valid point in route
            {
                // check if run out of route on misaligned switch

                if (newRoute.Count > 0)
                {
                    // get last section, and get next expected section
                    TrackCircuitSection lastSection = signalRef.TrackCircuitList[newRoute[newRoute.Count - 1].TCSectionIndex];
                    int nextSectionIndex = lastSection.ActivePins[newRoute[newRoute.Count - 1].Direction, 0].Link;

                    if (nextSectionIndex >= 0)
                    {
                        TrackCircuitSection nextSection = signalRef.TrackCircuitList[nextSectionIndex];

                        // is next expected section misaligned switch and is present section trailing end of this switch
                        if (nextSectionIndex == MisalignedSwitch[direction, 0] && lastSection.Index == MisalignedSwitch[direction, 1] &&
                            nextSection.ActivePins[0, 0].Link == requiredPosition.TCSectionIndex)
                        {

                            // misaligned switch

                            // reset indication
                            MisalignedSwitch[direction, 0] = -1;
                            MisalignedSwitch[direction, 1] = -1;

                            // set to out of control
                            SetTrainOutOfControl(OUTOFCONTROL.MISALIGNED_SWITCH);

                            // recalculate track position
                            UpdateTrainPosition();

                            // rebuild this list
                            UpdateSectionStateManual();

                            // exit

                            return (newRoute);
                        }
                    }
                }


                if (requiredRoute != null && requiredRoute.Count > 0)  // if route defined, then breakdown route
                {
                    signalRef.BreakDownRouteList(requiredRoute, 0, thisRouted);
                    requiredRoute.Clear();
                }


                // build new route

                MisalignedSwitch[direction, 0] = -1;
                MisalignedSwitch[direction, 1] = -1;

                List<int> tempSections = new List<int>();
                tempSections = signalRef.ScanRoute(this, requiredPosition.TCSectionIndex, requiredPosition.TCOffset,
                        requiredPosition.TCDirection, forward, minCheckDistanceManualM, true, false,
                        true, false, true, false, false, false, false, IsFreight);

                if (tempSections.Count > 0)
                {

                    // create subpath route

                    int prevSection = -2;    // preset to invalid

                    foreach (int sectionIndex in tempSections)
                    {
                        int sectionDirection = sectionIndex > 0 ? 0 : 1;
                        thisElement = new TCRouteElement(signalRef.TrackCircuitList[Math.Abs(sectionIndex)],
                                sectionDirection, signalRef, prevSection);
                        newRoute.Add(thisElement);
                        prevSection = Math.Abs(sectionIndex);
                    }
                }
            }
            // remove any sections before present position - train has passed over these sections
            else if (thisRouteIndex > 0)
            {
                for (int iindex = thisRouteIndex - 1; iindex >= 0; iindex--)
                {
                    newRoute.RemoveAt(iindex);
                }
            }

            // check if route ends at signal, determine length

            totalLengthM = 0;
            thisSection = signalRef.TrackCircuitList[requiredPosition.TCSectionIndex];
            offsetM = direction == 0 ? requiredPosition.TCOffset : thisSection.Length - requiredPosition.TCOffset;
            bool endWithSignal = false;    // ends with signal at STOP
            bool hasEndSignal = false;     // ends with cleared signal
            int sectionWithSignalIndex = 0;

            SignalObject previousSignal = new SignalObject(signalRef.ORTSSignalTypeCount);

            for (int iindex = 0; iindex < newRoute.Count && !endWithSignal; iindex++)
            {
                thisElement = newRoute[iindex];

                thisSection = signalRef.TrackCircuitList[thisElement.TCSectionIndex];
                totalLengthM += (thisSection.Length - offsetM);
                offsetM = 0.0f; // reset offset for further sections

                reqDirection = thisElement.Direction;
                if (thisSection.EndSignals[reqDirection] != null)
                {
                    var endSignal = thisSection.EndSignals[reqDirection];
                    MstsSignalAspect thisAspect = thisSection.EndSignals[reqDirection].this_sig_lr(MstsSignalFunction.NORMAL);
                    hasEndSignal = true;
                    if (previousSignal.signalRef != null) previousSignal.sigfound[(int)MstsSignalFunction.NORMAL] = endSignal.thisRef;
                    previousSignal = thisSection.EndSignals[reqDirection];

                    if (thisAspect == MstsSignalAspect.STOP && endSignal.hasPermission != SignalObject.Permission.Granted)
                    {
                        endWithSignal = true;
                        sectionWithSignalIndex = iindex;
                    }
                    else if (endSignal.enabledTrain == null && endSignal.hasFixedRoute) // signal cleared by default - make sure train is set
                    {
                        endSignal.enabledTrain = thisRouted;
                        endSignal.SetDefaultRoute();
                    }
                }
            }

            // check if signal is in last section
            // if not, probably moved forward beyond a signal, so remove all beyond first signal

            if (endWithSignal && sectionWithSignalIndex < newRoute.Count - 1)
            {
                for (int iindex = newRoute.Count - 1; iindex >= sectionWithSignalIndex + 1; iindex--)
                {
                    thisSection = signalRef.TrackCircuitList[newRoute[iindex].TCSectionIndex];
                    thisSection.RemoveTrain(this, true);
                    newRoute.RemoveAt(iindex);
                }
            }

            // if route does not end with signal and is too short, extend

            if (!endWithSignal && totalLengthM < minCheckDistanceManualM)
            {

                float extendedDistanceM = minCheckDistanceManualM - totalLengthM;
                TCRouteElement lastElement = newRoute[newRoute.Count - 1];

                int lastSectionIndex = lastElement.TCSectionIndex;
                TrackCircuitSection lastSection = signalRef.TrackCircuitList[lastSectionIndex];

                int nextSectionIndex = lastSection.Pins[lastElement.OutPin[0], lastElement.OutPin[1]].Link;
                int nextSectionDirection = lastSection.Pins[lastElement.OutPin[0], lastElement.OutPin[1]].Direction;

                // check if last item is non-aligned switch

                MisalignedSwitch[direction, 0] = -1;
                MisalignedSwitch[direction, 1] = -1;

                TrackCircuitSection nextSection = nextSectionIndex >= 0 ? signalRef.TrackCircuitList[nextSectionIndex] : null;
                if (nextSection != null && nextSection.CircuitType == TrackCircuitSection.TrackCircuitType.Junction)
                {
                    if (nextSection.Pins[0, 0].Link != lastSectionIndex &&
                        nextSection.Pins[1, nextSection.JunctionLastRoute].Link != lastSectionIndex)
                    {
                        MisalignedSwitch[direction, 0] = nextSection.Index;
                        MisalignedSwitch[direction, 1] = lastSectionIndex;
                    }
                }

                List<int> tempSections = new List<int>();

                if (nextSectionIndex >= 0 && MisalignedSwitch[direction, 0] < 0)
                {
                    bool reqAutoAlign = hasEndSignal; // auto-align switchs if route is extended from signal

                    tempSections = signalRef.ScanRoute(this, nextSectionIndex, 0,
                            nextSectionDirection, forward, extendedDistanceM, true, reqAutoAlign,
                            true, false, true, false, false, false, false, IsFreight);
                }

                if (tempSections.Count > 0)
                {
                    // add new sections

                    int prevSection = lastElement.TCSectionIndex;

                    foreach (int sectionIndex in tempSections)
                    {
                        thisElement = new Train.TCRouteElement(signalRef.TrackCircuitList[Math.Abs(sectionIndex)],
                                sectionIndex > 0 ? 0 : 1, signalRef, prevSection);
                        newRoute.Add(thisElement);
                        prevSection = Math.Abs(sectionIndex);
                    }
                }
            }

            // if route is too long, remove sections at end

            else if (totalLengthM > minCheckDistanceManualM)
            {
                float remainingLengthM = totalLengthM - signalRef.TrackCircuitList[newRoute[0].TCSectionIndex].Length; // do not count first section
                bool lengthExceeded = remainingLengthM > minCheckDistanceManualM;

                for (int iindex = newRoute.Count - 1; iindex > 1 && lengthExceeded; iindex--)
                {
                    thisElement = newRoute[iindex];
                    thisSection = signalRef.TrackCircuitList[thisElement.TCSectionIndex];

                    if ((remainingLengthM - thisSection.Length) > minCheckDistanceManualM)
                    {
                        remainingLengthM -= thisSection.Length;
                        newRoute.RemoveAt(iindex);
                    }
                    else
                    {
                        lengthExceeded = false;
                    }
                }
            }

            // route created to signal or max length, now check availability
            // check if other train in first section

            if (newRoute.Count > 0)
            {
                thisElement = newRoute[0];
                thisSection = signalRef.TrackCircuitList[thisElement.TCSectionIndex];
                reqDirection = forward ? thisElement.Direction : (thisElement.Direction == 0 ? 1 : 0);
                offsetM = direction == 0 ? requiredPosition.TCOffset : thisSection.Length - requiredPosition.TCOffset;

                Dictionary<Train, float> firstTrainInfo = thisSection.TestTrainAhead(this, offsetM, reqDirection);
                if (firstTrainInfo.Count > 0)
                {
                    foreach (KeyValuePair<Train, float> thisTrainAhead in firstTrainInfo)  // there is only one value
                    {
                        endAuthority = END_AUTHORITY.TRAIN_AHEAD;
                        endAuthorityDistanceM = thisTrainAhead.Value;
                        if (!thisSection.CircuitState.ThisTrainOccupying(this))
                            thisSection.PreReserve(thisRouted);
                    }
                    RemoveSignalEnablings(0, newRoute);
                }

                // check route availability
                // reserve sections which are available

                else
                {
                    int lastValidSectionIndex = 0;
                    bool isAvailable = true;
                    totalLengthM = 0;

                    for (int iindex = 0; iindex < newRoute.Count && isAvailable; iindex++)
                    {
                        thisSection = signalRef.TrackCircuitList[newRoute[iindex].TCSectionIndex];

                        if (isAvailable)
                        {
                            if (thisSection.IsAvailable(this))
                            {
                                lastValidSectionIndex = iindex;
                                totalLengthM += (thisSection.Length - offsetM);
                                offsetM = 0;
                                thisSection.Reserve(thisRouted, newRoute);
                            }
                            else
                            {
                                isAvailable = false;
                            }
                        }
                    }

                    // set default authority to max distance
                    endAuthority = END_AUTHORITY.MAX_DISTANCE;
                    endAuthorityDistanceM = totalLengthM;

                    // if last section ends with signal, set authority to signal
                    thisElement = newRoute[lastValidSectionIndex];
                    thisSection = signalRef.TrackCircuitList[thisElement.TCSectionIndex];
                    reqDirection = forward ? thisElement.Direction : (thisElement.Direction == 0 ? 1 : 0);
                    // last section ends with signal
                    if (thisSection.EndSignals[reqDirection] != null)
                    {
                        endAuthority = END_AUTHORITY.SIGNAL;
                        endAuthorityDistanceM = totalLengthM;
                    }

                    // sections not clear - check if end has signal

                    else
                    {

                        TrackCircuitSection nextSection = null;
                        TCRouteElement nextElement = null;

                        if (lastValidSectionIndex < newRoute.Count - 1)
                        {
                            nextElement = newRoute[lastValidSectionIndex + 1];
                            nextSection = signalRef.TrackCircuitList[nextElement.TCSectionIndex];
                        }

                        // check for end authority if not ended with signal
                        // last section is end of track
                        if (thisSection.CircuitType == TrackCircuitSection.TrackCircuitType.EndOfTrack)
                        {
                            endAuthority = END_AUTHORITY.END_OF_TRACK;
                            endAuthorityDistanceM = totalLengthM;
                        }

                        // first non-available section is switch or crossover
                        else if (nextSection != null && (nextSection.CircuitType == TrackCircuitSection.TrackCircuitType.Junction ||
                                     nextSection.CircuitType == TrackCircuitSection.TrackCircuitType.Crossover))
                        {
                            endAuthority = END_AUTHORITY.RESERVED_SWITCH;
                            endAuthorityDistanceM = totalLengthM;
                        }

                        // set authority is end of path unless train ahead
                        else
                        {
                            endAuthority = END_AUTHORITY.END_OF_PATH;
                            endAuthorityDistanceM = totalLengthM;

                            // check if train ahead not moving in opposite direction, in first non-available section

                            if (nextSection != null)
                            {
                                int oppositeDirection = forward ? (nextElement.Direction == 0 ? 1 : 0) : (nextElement.Direction == 0 ? 0 : 1);
                                reqDirection = forward ? nextElement.Direction : (nextElement.Direction == 0 ? 1 : 0);

                                bool oppositeTrain = nextSection.CircuitState.HasTrainsOccupying(oppositeDirection, false);

                                if (!oppositeTrain)
                                {
                                    Dictionary<Train, float> nextTrainInfo = nextSection.TestTrainAhead(this, 0.0f, reqDirection);
                                    if (nextTrainInfo.Count > 0)
                                    {
                                        foreach (KeyValuePair<Train, float> thisTrainAhead in nextTrainInfo)  // there is only one value
                                        {
                                            endAuthority = END_AUTHORITY.TRAIN_AHEAD;
                                            endAuthorityDistanceM = thisTrainAhead.Value + totalLengthM;
                                            lastValidSectionIndex++;
                                            nextSection.PreReserve(thisRouted);
                                        }
                                        RemoveSignalEnablings(lastValidSectionIndex, newRoute);
                                    }
                                }
                            }
                        }
                    }

                    // remove invalid sections from route
                    if (lastValidSectionIndex < newRoute.Count - 1)
                    {
                        for (int iindex = newRoute.Count - 1; iindex > lastValidSectionIndex; iindex--)
                        {
                            newRoute.RemoveAt(iindex);
                        }
                    }
                }
            }

            // no valid route could be found
            else
            {
                endAuthority = END_AUTHORITY.NO_PATH_RESERVED;
                endAuthorityDistanceM = 0.0f;
            }

            return (newRoute);
        }

        //================================================================================================//
        /// <summary>
        /// Remove signal enablings for subsequent route sections.
        /// They were set before testing whether there is an occupying train
        /// </summary>

        private void RemoveSignalEnablings(int firstSection, TCSubpathRoute newRoute)
        {
            for (int iSection = firstSection; iSection <= newRoute.Count - 1; iSection++)
            {
                var thisRouteElement = newRoute[iSection];
                var thisRouteSection = signalRef.TrackCircuitList[thisRouteElement.TCSectionIndex];
                var thisReqDirection = thisRouteElement.Direction;
                if (thisRouteSection.EndSignals[thisReqDirection] != null)
                {
                    var endSignal = thisRouteSection.EndSignals[thisReqDirection];
                    if (endSignal.enabledTrain != null && endSignal.enabledTrain.Train == this) endSignal.enabledTrain = null;
                }
            }
        }

        //================================================================================================//
        /// <summary>
        /// Restore Manual Mode
        /// </summary>

        public void RestoreManualMode()
        {
            // get next signal

            // forward
            NextSignalObject[0] = null;
            for (int iindex = 0; iindex < ValidRoute[0].Count && NextSignalObject[0] == null; iindex++)
            {
                TCRouteElement thisElement = ValidRoute[0][iindex];
                TrackCircuitSection thisSection = signalRef.TrackCircuitList[thisElement.TCSectionIndex];
                NextSignalObject[0] = thisSection.EndSignals[thisElement.Direction];
            }

            // backward
            NextSignalObject[1] = null;
            for (int iindex = 0; iindex < ValidRoute[1].Count && NextSignalObject[1] == null; iindex++)
            {
                TCRouteElement thisElement = ValidRoute[1][iindex];
                TrackCircuitSection thisSection = signalRef.TrackCircuitList[thisElement.TCSectionIndex];
                NextSignalObject[1] = thisSection.EndSignals[thisElement.Direction];
            }
        }


        //================================================================================================//
        //
        // Request signal permission in manual mode
        //

        public void RequestManualSignalPermission(ref TCSubpathRoute selectedRoute, int routeIndex)
        {

            // check if route ends with signal at danger

            TCRouteElement lastElement = selectedRoute[selectedRoute.Count - 1];
            TrackCircuitSection lastSection = signalRef.TrackCircuitList[lastElement.TCSectionIndex];

            // no signal in required direction at end of path

            if (lastSection.EndSignals[lastElement.Direction] == null)
            {
                if (Simulator.Confirmer != null) // As Confirmer may not be created until after a restore.
                    Simulator.Confirmer.Message(ConfirmLevel.Information, Simulator.Catalog.GetString("No signal in train's path"));
                return;
            }

            var requestedSignal = lastSection.EndSignals[lastElement.Direction];
            if (requestedSignal.enabledTrain != null && requestedSignal.enabledTrain.Train != this)
            {
                if (Simulator.Confirmer != null) // As Confirmer may not be created until after a restore.
                    Simulator.Confirmer.Message(ConfirmLevel.Warning, Simulator.Catalog.GetString("Next signal already allocated to other train"));
                Simulator.SoundNotify = Event.PermissionDenied;
                return;
            }

            requestedSignal.enabledTrain = routeIndex == 0 ? routedForward : routedBackward;
            requestedSignal.signalRoute.Clear();
            requestedSignal.holdState = SignalObject.HoldState.None;
            requestedSignal.hasPermission = SignalObject.Permission.Requested;

            // get route from next signal - extend to next signal or maximum length

            // first, get present length (except first section)

            float totalLengthM = 0;
            for (int iindex = 1; iindex < selectedRoute.Count; iindex++)
            {
                TrackCircuitSection thisSection = signalRef.TrackCircuitList[selectedRoute[iindex].TCSectionIndex];
                totalLengthM += thisSection.Length;
            }

            float remainingLengthM =
                Math.Min(minCheckDistanceManualM, Math.Max((minCheckDistanceManualM - totalLengthM), (minCheckDistanceManualM * 0.25f)));

            // get section behind signal

            int nextSectionIndex = lastSection.Pins[lastElement.OutPin[0], lastElement.OutPin[1]].Link;
            int nextSectionDirection = lastSection.Pins[lastElement.OutPin[0], lastElement.OutPin[1]].Direction;

            bool requestValid = false;

            // get route from signal - set remaining length or upto next signal

            if (nextSectionIndex > 0)
            {
                List<int> tempSections = signalRef.ScanRoute(this, nextSectionIndex, 0,
                    nextSectionDirection, true, remainingLengthM, true, true,
                    true, false, true, false, false, false, false, IsFreight);

                // set as signal route

                if (tempSections.Count > 0)
                {
                    int prevSection = -1;

                    foreach (int sectionIndex in tempSections)
                    {
                        TCRouteElement thisElement = new Train.TCRouteElement(signalRef.TrackCircuitList[Math.Abs(sectionIndex)],
                                sectionIndex > 0 ? 0 : 1, signalRef, prevSection);
                        requestedSignal.signalRoute.Add(thisElement);
                        selectedRoute.Add(thisElement);
                        prevSection = Math.Abs(sectionIndex);
                    }

                    requestedSignal.checkRouteState(false, requestedSignal.signalRoute, routedForward);
                    requestValid = true;
                }

                if (!requestValid)
                {
                    if (Simulator.Confirmer != null) // As Confirmer may not be created until after a restore.
                        Simulator.Confirmer.Message(ConfirmLevel.Information, Simulator.Catalog.GetString("Request to clear signal cannot be processed"));
                    Simulator.SoundNotify = Event.PermissionDenied;
                }
            }
        }

        //================================================================================================//
        /// <summary>
        /// Process request to set switch in manual mode
        /// Request may contain direction or actual node
        /// </summary>
        public bool ProcessRequestManualSetSwitch(Direction direction)
        {
            // find first switch in required direction

            TrackCircuitSection reqSwitch = null;
            int routeDirectionIndex = direction == Direction.Forward ? 0 : 1;
            bool switchSet = false;

            for (int iindex = 0; iindex < ValidRoute[routeDirectionIndex].Count && reqSwitch == null; iindex++)
            {
                TrackCircuitSection thisSection = signalRef.TrackCircuitList[ValidRoute[routeDirectionIndex][iindex].TCSectionIndex];
                if (thisSection.CircuitType == TrackCircuitSection.TrackCircuitType.Junction)
                {
                    reqSwitch = thisSection;
                }
            }

            if (reqSwitch == null)
            {
                // search beyond last section for switch using default pins (continue through normal sections only)

                TCRouteElement thisElement = ValidRoute[routeDirectionIndex][ValidRoute[routeDirectionIndex].Count - 1];
                TrackCircuitSection lastSection = signalRef.TrackCircuitList[thisElement.TCSectionIndex];
                int curDirection = thisElement.Direction;
                int nextSectionIndex = thisElement.TCSectionIndex;

                bool validRoute = lastSection.CircuitType == TrackCircuitSection.TrackCircuitType.Normal;

                while (reqSwitch == null && validRoute)
                {
                    if (lastSection.CircuitType == TrackCircuitSection.TrackCircuitType.Crossover)
                    {
                        int outPinIndex = curDirection == 0 ? 1 : 0;
                        if (lastSection.Pins[curDirection, 0].Link == nextSectionIndex)
                        {
                            nextSectionIndex = lastSection.Pins[outPinIndex, 0].Link;
                            curDirection = lastSection.Pins[outPinIndex, 0].Direction;
                        }
                        else if (lastSection.Pins[curDirection, 1].Link == nextSectionIndex)
                        {
                            nextSectionIndex = lastSection.Pins[outPinIndex, 1].Link;
                            curDirection = lastSection.Pins[outPinIndex, 1].Direction;
                        }
                    }
                    else
                    {
                        nextSectionIndex = lastSection.Pins[curDirection, 0].Link;
                        curDirection = lastSection.ActivePins[curDirection, 0].Direction;
                        lastSection = signalRef.TrackCircuitList[nextSectionIndex];
                    }

                    if (lastSection.CircuitType == TrackCircuitSection.TrackCircuitType.Junction)
                    {
                        reqSwitch = lastSection;
                    }
                    else if (lastSection.CircuitType != TrackCircuitSection.TrackCircuitType.Normal)
                    {
                        validRoute = false;
                    }
                }
            }

            if (reqSwitch != null)
            {
                // check if switch is clear
                if (!reqSwitch.CircuitState.HasTrainsOccupying() && reqSwitch.CircuitState.TrainReserved == null && reqSwitch.CircuitState.SignalReserved < 0)
                {
                    reqSwitch.JunctionSetManual = reqSwitch.JunctionLastRoute == 0 ? 1 : 0;
                    signalRef.setSwitch(reqSwitch.OriginalIndex, reqSwitch.JunctionSetManual, reqSwitch);
                    switchSet = true;
                }
                // check if switch reserved by this train - if so, dealign and breakdown route
                else if (reqSwitch.CircuitState.TrainReserved != null && reqSwitch.CircuitState.TrainReserved.Train == this)
                {
                    int reqRouteIndex = reqSwitch.CircuitState.TrainReserved.TrainRouteDirectionIndex;
                    int routeIndex = ValidRoute[reqRouteIndex].GetRouteIndex(reqSwitch.Index, 0);
                    signalRef.BreakDownRouteList(ValidRoute[reqRouteIndex], routeIndex, reqSwitch.CircuitState.TrainReserved);
                    if (routeIndex >= 0 && ValidRoute[reqRouteIndex].Count > routeIndex)
                        ValidRoute[reqRouteIndex].RemoveRange(routeIndex, ValidRoute[reqRouteIndex].Count - routeIndex);
                    else Trace.TraceWarning("Switch index {0} could not be found in ValidRoute[{1}]; routeDirectionIndex = {2}",
                            reqSwitch.Index, reqRouteIndex, routeDirectionIndex);
                    reqSwitch.deAlignSwitchPins();
                    reqSwitch.JunctionSetManual = reqSwitch.JunctionLastRoute == 0 ? 1 : 0;
                    signalRef.setSwitch(reqSwitch.OriginalIndex, reqSwitch.JunctionSetManual, reqSwitch);
                    switchSet = true;
                }

                if (switchSet)
                    ProcessManualSwitch(routeDirectionIndex, reqSwitch, direction);
                if (Simulator.Confirmer != null) // As Confirmer may not be created until after a restore.
                    Simulator.Confirmer.Confirm(
                        (direction == Direction.Forward) ? CabControl.SwitchAhead : CabControl.SwitchBehind,
                        CabSetting.On);
            }
            else
            {
                if (Simulator.Confirmer != null) // As Confirmer may not be created until after a restore.
                    Simulator.Confirmer.Message(ConfirmLevel.Warning, Simulator.Catalog.GetString("No switch found"));
            }

            return (switchSet);
        }

        public bool ProcessRequestManualSetSwitch(int reqSwitchIndex)
        {
            // find switch in route - forward first

            int routeDirectionIndex = -1;
            bool switchFound = false;
            Direction direction = Direction.N;

            for (int iindex = 0; iindex < ValidRoute[0].Count - 1 && !switchFound; iindex++)
            {
                if (ValidRoute[0][iindex].TCSectionIndex == reqSwitchIndex)
                {
                    routeDirectionIndex = 0;
                    direction = Direction.Forward;
                    switchFound = true;
                }
            }

            for (int iindex = 0; iindex < ValidRoute[1].Count - 1 && !switchFound; iindex++)
            {
                if (ValidRoute[1][iindex].TCSectionIndex == reqSwitchIndex)
                {
                    routeDirectionIndex = 1;
                    direction = Direction.Reverse;
                    switchFound = true;
                }
            }

            if (switchFound)
            {
                TrackCircuitSection reqSwitch = signalRef.TrackCircuitList[reqSwitchIndex];
                ProcessManualSwitch(routeDirectionIndex, reqSwitch, direction);
                return (true);
            }

            return (false);
        }

        //================================================================================================//
        /// <summary>
        /// Process switching of manual switch
        /// </summary>

        public void ProcessManualSwitch(int routeDirectionIndex, TrackCircuitSection switchSection, Direction direction)
        {
            TrainRouted thisRouted = direction == Direction.Reverse ? routedForward : routedBackward;
            TCSubpathRoute selectedRoute = ValidRoute[routeDirectionIndex];

            // store required position
            int reqSwitchPosition = switchSection.JunctionSetManual;

            // find index of section in present route
            int junctionIndex = selectedRoute.GetRouteIndex(switchSection.Index, 0);

            // check if any signals between train and switch
            List<SignalObject> signalsFound = new List<SignalObject>();

            for (int iindex = 0; iindex < junctionIndex; iindex++)
            {
                TCRouteElement thisElement = selectedRoute[iindex];
                TrackCircuitSection thisSection = signalRef.TrackCircuitList[thisElement.TCSectionIndex];
                int signalDirection = thisElement.Direction == 0 ? 0 : 1;

                if (thisSection.EndSignals[signalDirection] != null)
                {
                    signalsFound.Add(thisSection.EndSignals[signalDirection]);
                }
            }

            // if any signals found : reset signals

            foreach (SignalObject thisSignal in signalsFound)
            {
                thisSignal.ResetSignal(false);
            }

            // breakdown and clear route

            signalRef.BreakDownRouteList(selectedRoute, 0, thisRouted);
            selectedRoute.Clear();

            // restore required position (is cleared by route breakdown)
            switchSection.JunctionSetManual = reqSwitchPosition;

            // set switch
            switchSection.deAlignSwitchPins();
            signalRef.setSwitch(switchSection.OriginalIndex, switchSection.JunctionSetManual, switchSection);

            // reset indication for misaligned switch
            MisalignedSwitch[routeDirectionIndex, 0] = -1;
            MisalignedSwitch[routeDirectionIndex, 1] = -1;

            // build new route

            int routeIndex = -1;

            if (direction == Direction.Forward)
            {
                selectedRoute = CheckManualPath(0, PresentPosition[0], null, true, ref EndAuthorityType[0],
                    ref DistanceToEndNodeAuthorityM[0]);
                routeIndex = 0;

            }
            else
            {
                TCPosition tempRear = new TCPosition();
                PresentPosition[1].CopyTo(ref tempRear);
                tempRear.TCDirection = tempRear.TCDirection == 0 ? 1 : 0;
                selectedRoute = CheckManualPath(1, tempRear, null, true, ref EndAuthorityType[1],
                     ref DistanceToEndNodeAuthorityM[1]);
                routeIndex = 1;
            }

            // if route ends at previously cleared signal, request clear signal again

            TCRouteElement lastElement = selectedRoute[selectedRoute.Count - 1];
            TrackCircuitSection lastSection = signalRef.TrackCircuitList[lastElement.TCSectionIndex];
            int lastDirection = lastElement.Direction == 0 ? 0 : 1;

            var lastSignal = lastSection.EndSignals[lastDirection];

            while (lastSignal != null && signalsFound.Contains(lastSignal))
            {
                RequestManualSignalPermission(ref selectedRoute, routeIndex);

                lastElement = selectedRoute[selectedRoute.Count - 1];
                lastSection = signalRef.TrackCircuitList[lastElement.TCSectionIndex];
                lastDirection = lastElement.Direction == 0 ? 0 : 1;

                lastSignal = lastSection.EndSignals[lastDirection];
            }

            ValidRoute[routeDirectionIndex] = selectedRoute;
        }

        //================================================================================================//
        /// <summary>
        /// Update speed limit in manual mode
        /// </summary>

        public void CheckSpeedLimitManual(TCSubpathRoute routeBehind, TCSubpathRoute routeUnderTrain, float offsetStart,
            float reverseOffset, int passedSignalIndex, int routeDirection)
        {
            // check backward for last speedlimit in direction of train - raise speed if passed

            TCRouteElement thisElement = routeBehind[0];
            List<int> foundSpeedLimit = new List<int>();

            foundSpeedLimit = signalRef.ScanRoute(this, thisElement.TCSectionIndex, offsetStart, thisElement.Direction,
                    true, -1, false, true, false, false, false, false, false, false, true, IsFreight);

            if (foundSpeedLimit.Count > 0)
            {
                var speedLimit = signalRef.SignalObjects[Math.Abs(foundSpeedLimit[0])];
                var thisSpeedInfo = speedLimit.this_lim_speed(MstsSignalFunction.SPEED);
                float thisSpeedMpS = IsFreight ? thisSpeedInfo.speed_freight : thisSpeedInfo.speed_pass;

                if (thisSpeedMpS > 0)
                {
                    if (thisSpeedInfo.speed_noSpeedReductionOrIsTempSpeedReduction == 0) allowedMaxSpeedLimitMpS = thisSpeedMpS;
                    else allowedMaxTempSpeedLimitMpS = thisSpeedMpS;
                    if (Simulator.TimetableMode) AllowedMaxSpeedMpS = thisSpeedMpS;
                    else AllowedMaxSpeedMpS = Math.Min(allowedMaxSpeedLimitMpS, Math.Min(allowedMaxTempSpeedLimitMpS,
                                       allowedMaxSpeedSignalMpS == -1 ? 999 : allowedMaxSpeedSignalMpS));
                }
            }
            // No speed limits behind us, initialize allowedMaxSpeedLimitMpS.
            else if (!Simulator.TimetableMode)
            {
                AllowedMaxSpeedMpS = allowedMaxSpeedLimitMpS;
            }

            // check backward for last signal in direction of train - check with list of pending signal speeds
            // search also checks for speedlimit to see which is nearest train

            foundSpeedLimit.Clear();
            foundSpeedLimit = signalRef.ScanRoute(this, thisElement.TCSectionIndex, offsetStart, thisElement.Direction,
                    true, -1, false, true, false, false, false, false, true, false, true, IsFreight, true);

            if (foundSpeedLimit.Count > 0)
            {
                var thisSignal = signalRef.SignalObjects[Math.Abs(foundSpeedLimit[0])];
                if (thisSignal.isSignal)
                {
                    // if signal is now just behind train - set speed as signal speed limit, do not reenter in list
                    if (PassedSignalSpeeds.ContainsKey(thisSignal.thisRef))
                    {
                        allowedMaxSpeedSignalMpS = PassedSignalSpeeds[thisSignal.thisRef];
                        AllowedMaxSpeedMpS = Math.Min(allowedMaxSpeedSignalMpS, AllowedMaxSpeedMpS);
                        LastPassedSignal[routeDirection] = thisSignal.thisRef;
                    }
                    // if signal is not last passed signal - reset signal speed limit
                    else if (thisSignal.thisRef != LastPassedSignal[routeDirection])
                    {
                        allowedMaxSpeedSignalMpS = TrainMaxSpeedMpS;
                        LastPassedSignal[routeDirection] = -1;
                    }
                    // set signal limit as speed limit
                    else
                    {
                        AllowedMaxSpeedMpS = Math.Min(allowedMaxSpeedSignalMpS, AllowedMaxSpeedMpS);
                    }
                }
                else if (thisSignal.SignalHeads[0].sigFunction == MstsSignalFunction.SPEED)
                {
                    ObjectSpeedInfo thisSpeedInfo = thisSignal.this_sig_speed(MstsSignalFunction.SPEED);
                    if (thisSpeedInfo != null && thisSpeedInfo.speed_reset == 1)
                    {
                        allowedMaxSpeedSignalMpS = TrainMaxSpeedMpS;
                        if (Simulator.TimetableMode)
                            AllowedMaxSpeedMpS = allowedMaxSpeedLimitMpS;
                        else
                            AllowedMaxSpeedMpS = Math.Min(allowedMaxTempSpeedLimitMpS, allowedMaxSpeedLimitMpS);
                    }
                }
            }

            // check forward along train for speedlimit and signal in direction of train - limit speed if passed
            // loop as there might be more than one

            thisElement = routeUnderTrain[0];
            foundSpeedLimit.Clear();
            float remLength = Length;
            Dictionary<int, float> remainingSignals = new Dictionary<int, float>();

            foundSpeedLimit = signalRef.ScanRoute(this, thisElement.TCSectionIndex, reverseOffset, thisElement.Direction,
                    true, remLength, false, true, false, false, false, true, false, true, false, IsFreight);

            bool limitAlongTrain = true;
            while (foundSpeedLimit.Count > 0 && limitAlongTrain)
            {
                var thisObject = signalRef.SignalObjects[Math.Abs(foundSpeedLimit[0])];

                // check if not beyond end of train
                TrackCircuitSection reqSection = signalRef.TrackCircuitList[thisObject.TCReference];
                float speedLimitDistance = reqSection.GetDistanceBetweenObjects(thisElement.TCSectionIndex, reverseOffset, thisElement.Direction,
                    thisObject.TCReference, thisObject.TCOffset);
                if (speedLimitDistance > Length)
                {
                    limitAlongTrain = false;
                }
                else
                {
                    int nextSectionIndex = thisObject.TCReference;
                    int direction = thisObject.TCDirection;
                    float objectOffset = thisObject.TCOffset;

                    if (thisObject.isSignal)
                    {
                        nextSectionIndex = thisObject.TCNextTC;
                        direction = thisObject.TCNextDirection;
                        objectOffset = 0.0f;

                        if (PassedSignalSpeeds.ContainsKey(thisObject.thisRef))
                        {
                            allowedMaxSpeedSignalMpS = PassedSignalSpeeds[thisObject.thisRef];
                            if (Simulator.TimetableMode) AllowedMaxSpeedMpS = Math.Min(AllowedMaxSpeedMpS, allowedMaxSpeedSignalMpS);
                            else AllowedMaxSpeedMpS = Math.Min(allowedMaxSpeedLimitMpS, Math.Min(allowedMaxTempSpeedLimitMpS, allowedMaxSpeedSignalMpS));

                            if (!remainingSignals.ContainsKey(thisObject.thisRef))
                                remainingSignals.Add(thisObject.thisRef, allowedMaxSpeedSignalMpS);
                        }
                    }
                    else
                    {
                        ObjectSpeedInfo thisSpeedInfo = thisObject.this_lim_speed(MstsSignalFunction.SPEED);
                        float thisSpeedMpS = IsFreight ? thisSpeedInfo.speed_freight : thisSpeedInfo.speed_pass;
                        if (thisSpeedMpS > 0)
                        {
                            if (thisSpeedInfo.speed_noSpeedReductionOrIsTempSpeedReduction == 0) // standard speedpost
                            {
                                if (Simulator.TimetableMode)
                                {
                                    allowedMaxSpeedLimitMpS = Math.Min(allowedMaxSpeedLimitMpS, thisSpeedMpS);
                                    AllowedMaxSpeedMpS = allowedMaxSpeedLimitMpS;
                                }
                                else
                                {
                                    allowedMaxSpeedLimitMpS = Math.Min(allowedMaxSpeedLimitMpS, thisSpeedMpS);
                                    AllowedMaxSpeedMpS = Math.Min(allowedMaxSpeedLimitMpS, Math.Min(allowedMaxTempSpeedLimitMpS,
                                       allowedMaxSpeedSignalMpS == -1 ? 999 : allowedMaxSpeedSignalMpS));
                                }
                            }
                            else
                            {
                                allowedMaxTempSpeedLimitMpS = Math.Min(allowedMaxTempSpeedLimitMpS, thisSpeedMpS);
                                AllowedMaxSpeedMpS = Math.Min(allowedMaxSpeedLimitMpS, Math.Min(allowedMaxTempSpeedLimitMpS,
                                    allowedMaxSpeedSignalMpS == -1 ? 999 : allowedMaxSpeedSignalMpS));
                            }
                        }
                    }

                    remLength -= (thisObject.TCOffset - offsetStart);

                    foundSpeedLimit = signalRef.ScanRoute(this, nextSectionIndex, objectOffset, direction,
                        true, remLength, false, true, false, false, false, true, false, true, false, IsFreight);
                }
            }

            // set list of remaining signals as new pending list
            PassedSignalSpeeds.Clear();
            foreach (KeyValuePair<int, float> thisPair in remainingSignals)
            {
                if (!PassedSignalSpeeds.ContainsKey(thisPair.Key))
                    PassedSignalSpeeds.Add(thisPair.Key, thisPair.Value);
            }

            // check if signal passed posed a speed limit lower than present limit

            if (passedSignalIndex >= 0)
            {
                var passedSignal = signalRef.SignalObjects[passedSignalIndex];
                var thisSpeedInfo = passedSignal.this_sig_speed(MstsSignalFunction.NORMAL);

                if (thisSpeedInfo != null)
                {
                    float thisSpeedMpS = IsFreight ? thisSpeedInfo.speed_freight : thisSpeedInfo.speed_pass;
                    if (thisSpeedMpS > 0 && !PassedSignalSpeeds.ContainsKey(passedSignal.thisRef))
                    {
                        allowedMaxSpeedSignalMpS = allowedMaxSpeedSignalMpS > 0 ? Math.Min(allowedMaxSpeedSignalMpS, thisSpeedMpS) : thisSpeedMpS;
                        AllowedMaxSpeedMpS = Math.Min(AllowedMaxSpeedMpS, allowedMaxSpeedSignalMpS);

                        PassedSignalSpeeds.Add(passedSignal.thisRef, thisSpeedMpS);
                    }
                }
            }
        }

        //================================================================================================//
        /// <summary>
        /// Update section occupy states fore explorer mode
        /// Note : explorer mode has no distance actions so sections must be cleared immediately
        /// </summary>

        public void UpdateSectionStateExplorer()
        {
            // occupation is set in forward mode only
            // build route from rear to front - before reset occupy so correct switch alignment is used
            TrainRoute = signalRef.BuildTempRoute(this, PresentPosition[1].TCSectionIndex, PresentPosition[1].TCOffset,
                            PresentPosition[1].TCDirection, Length, false, true, false);

            // save present occupation list

            List<TrackCircuitSection> clearedSections = new List<TrackCircuitSection>();
            for (int iindex = OccupiedTrack.Count - 1; iindex >= 0; iindex--)
            {
                clearedSections.Add(OccupiedTrack[iindex]);
            }

            // first check for misaligned switch

            int reqDirection = MUDirection == Direction.Forward ? 0 : 1;
            foreach (TCRouteElement thisElement in TrainRoute)
            {
                TrackCircuitSection thisSection = signalRef.TrackCircuitList[thisElement.TCSectionIndex];

                // occupying misaligned switch : reset routes and position
                if (thisSection.Index == MisalignedSwitch[reqDirection, 0])
                {
                    // align switch
                    if (!MPManager.NoAutoSwitch()) thisSection.alignSwitchPins(MisalignedSwitch[reqDirection, 1]);
                    MisalignedSwitch[reqDirection, 0] = -1;
                    MisalignedSwitch[reqDirection, 1] = -1;

                    // recalculate track position
                    UpdateTrainPosition();

                    // rebuild this list
                    UpdateSectionStateExplorer();

                    // exit, as routine has called itself
                    return;
                }
            }

            // if all is well, set tracks to occupied

            OccupiedTrack.Clear();

            foreach (TCRouteElement thisElement in TrainRoute)
            {
                TrackCircuitSection thisSection = signalRef.TrackCircuitList[thisElement.TCSectionIndex];

                if (clearedSections.Contains(thisSection))
                {
                    thisSection.ResetOccupied(this); // reset occupation if it was occupied
                    clearedSections.Remove(thisSection);  // remove from cleared list
                }

                thisSection.Reserve(routedForward, TrainRoute);  // reserve first to reset switch alignments
                thisSection.SetOccupied(routedForward);
            }

            foreach (TrackCircuitSection exSection in clearedSections)
            {
                exSection.ClearOccupied(this, true); // sections really cleared
            }
        }

        //================================================================================================//
        /// <summary>
        /// Update Explorer Mode
        /// </summary>

        public void UpdateExplorerMode(int signalObjectIndex)
        {
            if (MPManager.IsMultiPlayer())
            // first unreserve all route positions where train is not present
            {
                if (ValidRoute[0] != null)
                {
                    foreach (var tcRouteElement in ValidRoute[0])
                    {
                        var tcSection = signalRef.TrackCircuitList[tcRouteElement.TCSectionIndex];
                        if (tcSection.CheckReserved(routedForward) && !tcSection.CircuitState.TrainOccupy.ContainsTrain(this))
                        {
                            tcSection.Unreserve();
                            tcSection.UnreserveTrain();
                        }
                    }
                }
                if (ValidRoute[1] != null)
                {
                    foreach (var tcRouteElement in ValidRoute[1])
                    {
                        var tcSection = signalRef.TrackCircuitList[tcRouteElement.TCSectionIndex];
                        if (tcSection.CheckReserved(routedBackward) && !tcSection.CircuitState.TrainOccupy.ContainsTrain(this))                        {
                            tcSection.Unreserve();
                            tcSection.UnreserveTrain();
                        }
                    }
                }
            }

            // check present forward
            TCSubpathRoute newRouteF = CheckExplorerPath(0, PresentPosition[0], ValidRoute[0], true, ref EndAuthorityType[0],
                ref DistanceToEndNodeAuthorityM[0]);
            ValidRoute[0] = newRouteF;
            int routeIndex = ValidRoute[0].GetRouteIndex(PresentPosition[0].TCSectionIndex, 0);
            PresentPosition[0].RouteListIndex = routeIndex;

            // check present reverse
            // reverse present rear position direction to build correct path backwards
            TCPosition tempRear = new TCPosition();
            PresentPosition[1].CopyTo(ref tempRear);
            tempRear.TCDirection = tempRear.TCDirection == 0 ? 1 : 0;
            TCSubpathRoute newRouteR = CheckExplorerPath(1, tempRear, ValidRoute[1], true, ref EndAuthorityType[1],
                ref DistanceToEndNodeAuthorityM[1]);
            ValidRoute[1] = newRouteR;

            // select valid route

            if (MUDirection == Direction.Forward)
            {
                // use position from other end of section
                float reverseOffset = signalRef.TrackCircuitList[PresentPosition[1].TCSectionIndex].Length - PresentPosition[1].TCOffset;
                CheckSpeedLimitManual(ValidRoute[1], TrainRoute, reverseOffset, PresentPosition[1].TCOffset, signalObjectIndex, 0);
            }
            else
            {
                TCSubpathRoute tempRoute = new TCSubpathRoute(); // reversed trainRoute
                for (int iindex = TrainRoute.Count - 1; iindex >= 0; iindex--)
                {
                    TCRouteElement thisElement = TrainRoute[iindex];
                    thisElement.Direction = thisElement.Direction == 0 ? 1 : 0;
                    tempRoute.Add(thisElement);
                }
                float reverseOffset = signalRef.TrackCircuitList[PresentPosition[0].TCSectionIndex].Length - PresentPosition[0].TCOffset;
                CheckSpeedLimitManual(ValidRoute[0], tempRoute, PresentPosition[0].TCOffset, reverseOffset, signalObjectIndex, 1);
            }

            // reset signal permission

            if (signalObjectIndex >= 0)
            {
                var thisSignal = signalRef.SignalObjects[signalObjectIndex];
                thisSignal.hasPermission = SignalObject.Permission.Denied;

                thisSignal.resetSignalEnabled();
            }

            // get next signal

            // forward
            NextSignalObject[0] = null;
            for (int iindex = 0; iindex < ValidRoute[0].Count && NextSignalObject[0] == null; iindex++)
            {
                TCRouteElement thisElement = ValidRoute[0][iindex];
                TrackCircuitSection thisSection = signalRef.TrackCircuitList[thisElement.TCSectionIndex];
                NextSignalObject[0] = thisSection.EndSignals[thisElement.Direction];
            }

            // backward
            NextSignalObject[1] = null;
            for (int iindex = 0; iindex < ValidRoute[1].Count && NextSignalObject[1] == null; iindex++)
            {
                TCRouteElement thisElement = ValidRoute[1][iindex];
                TrackCircuitSection thisSection = signalRef.TrackCircuitList[thisElement.TCSectionIndex];
                NextSignalObject[1] = thisSection.EndSignals[thisElement.Direction];
            }

            // clear all build up distance actions
            requiredActions.RemovePendingAIActionItems(true);
        }

        //================================================================================================//
        /// <summary>
        /// Check Explorer Path
        /// <\summary>

        public TCSubpathRoute CheckExplorerPath(int direction, TCPosition requiredPosition, TCSubpathRoute requiredRoute, bool forward,
            ref END_AUTHORITY endAuthority, ref float endAuthorityDistanceM)
        {
            TrainRouted thisRouted = direction == 0 ? routedForward : routedBackward;

            // create new route or set to existing route

            TCSubpathRoute newRoute = null;

            TCRouteElement thisElement = null;
            TrackCircuitSection thisSection = null;
            int reqDirection = 0;
            float offsetM = 0.0f;
            float totalLengthM = 0.0f;

            if (requiredRoute == null)
            {
                newRoute = new TCSubpathRoute();
            }
            else
            {
                newRoute = requiredRoute;
            }

            // check if train on valid position in route

            int thisRouteIndex = newRoute.GetRouteIndex(requiredPosition.TCSectionIndex, 0);
            if (thisRouteIndex < 0)    // no valid point in route
            {
                if (requiredRoute != null && requiredRoute.Count > 0)  // if route defined, then breakdown route
                {
                    signalRef.BreakDownRouteList(requiredRoute, 0, thisRouted);
                    requiredRoute.Clear();
                }

                // build new route

                List<int> tempSections = new List<int>();

                tempSections = signalRef.ScanRoute(this, requiredPosition.TCSectionIndex, requiredPosition.TCOffset,
                        requiredPosition.TCDirection, forward, minCheckDistanceM, true, false,
                        false, false, true, false, false, false, false, IsFreight);

                if (tempSections.Count > 0)
                {

                    // create subpath route

                    int prevSection = -2;    // preset to invalid

                    foreach (int sectionIndex in tempSections)
                    {
                        int sectionDirection = sectionIndex > 0 ? 0 : 1;
                        thisElement = new TCRouteElement(signalRef.TrackCircuitList[Math.Abs(sectionIndex)],
                                sectionDirection, signalRef, prevSection);
                        newRoute.Add(thisElement);
                        prevSection = Math.Abs(sectionIndex);
                    }
                }
            }
            // remove any sections before present position - train has passed over these sections
            else if (thisRouteIndex > 0)
            {
                for (int iindex = thisRouteIndex - 1; iindex >= 0; iindex--)
                {
                    newRoute.RemoveAt(iindex);
                }
            }

            // check if route ends at signal, determine length

            totalLengthM = 0;
            thisSection = signalRef.TrackCircuitList[requiredPosition.TCSectionIndex];
            offsetM = direction == 0 ? requiredPosition.TCOffset : thisSection.Length - requiredPosition.TCOffset;
            bool endWithSignal = false;    // ends with signal at STOP
            bool hasEndSignal = false;     // ends with cleared signal
            int sectionWithSignalIndex = 0;

            for (int iindex = 0; iindex < newRoute.Count && !endWithSignal; iindex++)
            {
                thisElement = newRoute[iindex];

                thisSection = signalRef.TrackCircuitList[thisElement.TCSectionIndex];
                totalLengthM += (thisSection.Length - offsetM);
                offsetM = 0.0f; // reset offset for further sections

                // check on state of signals
                // also check if signal properly enabled

                reqDirection = thisElement.Direction;
                if (thisSection.EndSignals[reqDirection] != null)
                {
                    var endSignal = thisSection.EndSignals[reqDirection];
                    var thisAspect = thisSection.EndSignals[reqDirection].this_sig_lr(MstsSignalFunction.NORMAL);
                    hasEndSignal = true;

                    if (thisAspect == MstsSignalAspect.STOP && endSignal.hasPermission != SignalObject.Permission.Granted)
                    {
                        endWithSignal = true;
                        sectionWithSignalIndex = iindex;
                    }
                    else if (!endSignal.enabled)   // signal cleared by default only - request for proper clearing
                    {
                        endSignal.requestClearSignalExplorer(newRoute, 0.0f, thisRouted, true, 0);  // do NOT propagate
                    }

                }
            }

            // check if signal is in last section
            // if not, probably moved forward beyond a signal, so remove all beyond first signal

            if (endWithSignal && sectionWithSignalIndex < newRoute.Count - 1)
            {
                for (int iindex = newRoute.Count - 1; iindex >= sectionWithSignalIndex + 1; iindex--)
                {
                    thisSection = signalRef.TrackCircuitList[newRoute[iindex].TCSectionIndex];
                    thisSection.RemoveTrain(this, true);
                    newRoute.RemoveAt(iindex);
                }
            }

            // if route does not end with signal and is too short, extend

            if (!endWithSignal && totalLengthM < minCheckDistanceM)
            {

                float extendedDistanceM = minCheckDistanceM - totalLengthM;
                TCRouteElement lastElement = newRoute[newRoute.Count - 1];

                int lastSectionIndex = lastElement.TCSectionIndex;
                TrackCircuitSection lastSection = signalRef.TrackCircuitList[lastSectionIndex];

                int nextSectionIndex = lastSection.Pins[lastElement.OutPin[0], lastElement.OutPin[1]].Link;
                int nextSectionDirection = lastSection.Pins[lastElement.OutPin[0], lastElement.OutPin[1]].Direction;

                // check if last item is non-aligned switch

                MisalignedSwitch[direction, 0] = -1;
                MisalignedSwitch[direction, 1] = -1;

                TrackCircuitSection nextSection = nextSectionIndex >= 0 ? signalRef.TrackCircuitList[nextSectionIndex] : null;
                if (nextSection != null && nextSection.CircuitType == TrackCircuitSection.TrackCircuitType.Junction)
                {
                    if (nextSection.Pins[0, 0].Link != lastSectionIndex &&
                        nextSection.Pins[0, 1].Link != lastSectionIndex &&
                        nextSection.Pins[1, nextSection.JunctionLastRoute].Link != lastSectionIndex)
                    {
                        MisalignedSwitch[direction, 0] = nextSection.Index;
                        MisalignedSwitch[direction, 1] = lastSectionIndex;
                    }
                }

                List<int> tempSections = new List<int>();

                if (nextSectionIndex >= 0 && MisalignedSwitch[direction, 0] < 0)
                {
                    bool reqAutoAlign = hasEndSignal; // auto-align switches if route is extended from signal

                    tempSections = signalRef.ScanRoute(this, nextSectionIndex, 0,
                            nextSectionDirection, forward, extendedDistanceM, true, reqAutoAlign,
                            true, false, true, false, false, false, false, IsFreight);
                }

                if (tempSections.Count > 0)
                {
                    // add new sections

                    int prevSection = lastElement.TCSectionIndex;

                    foreach (int sectionIndex in tempSections)
                    {
                        thisElement = new Train.TCRouteElement(signalRef.TrackCircuitList[Math.Abs(sectionIndex)],
                                sectionIndex > 0 ? 0 : 1, signalRef, prevSection);
                        newRoute.Add(thisElement);
                        prevSection = Math.Abs(sectionIndex);
                    }
                }
            }

            // if route is too long, remove sections at end

            else if (totalLengthM > minCheckDistanceM)
            {
                float remainingLengthM = totalLengthM - signalRef.TrackCircuitList[newRoute[0].TCSectionIndex].Length; // do not count first section
                bool lengthExceeded = remainingLengthM > minCheckDistanceM;

                for (int iindex = newRoute.Count - 1; iindex > 1 && lengthExceeded; iindex--)
                {
                    thisElement = newRoute[iindex];
                    thisSection = signalRef.TrackCircuitList[thisElement.TCSectionIndex];

                    if ((remainingLengthM - thisSection.Length) > minCheckDistanceM)
                    {
                        remainingLengthM -= thisSection.Length;
                        newRoute.RemoveAt(iindex);
                    }
                    else
                    {
                        lengthExceeded = false;
                    }
                }
            }

            // check for any uncleared signals in route - if first found, request clear signal

            bool unclearedSignal = false;
            int signalIndex = newRoute.Count - 1;
            int nextUnclearSignalIndex = -1;

            for (int iindex = 0; iindex <= newRoute.Count - 1 && !unclearedSignal; iindex++)
            {
                thisElement = newRoute[iindex];
                thisSection = signalRef.TrackCircuitList[thisElement.TCSectionIndex];

                var nextSignal = thisSection.EndSignals[thisElement.Direction];
                if (nextSignal != null &&
                    nextSignal.this_sig_lr(MstsSignalFunction.NORMAL) == MstsSignalAspect.STOP &&
                    nextSignal.hasPermission != SignalObject.Permission.Granted)
                {
                    unclearedSignal = true;
                    signalIndex = iindex;
                    nextUnclearSignalIndex = nextSignal.thisRef;
                }
            }

            // route created to signal or max length, now check availability - but only up to first unclear signal
            // check if other train in first section

            if (newRoute.Count > 0)
            {
                thisElement = newRoute[0];
                thisSection = signalRef.TrackCircuitList[thisElement.TCSectionIndex];
                reqDirection = forward ? thisElement.Direction : (thisElement.Direction == 0 ? 1 : 0);
                offsetM = direction == 0 ? requiredPosition.TCOffset : thisSection.Length - requiredPosition.TCOffset;

                Dictionary<Train, float> firstTrainInfo = thisSection.TestTrainAhead(this, offsetM, reqDirection);
                if (firstTrainInfo.Count > 0)
                {
                    foreach (KeyValuePair<Train, float> thisTrainAhead in firstTrainInfo)  // there is only one value
                    {
                        endAuthority = END_AUTHORITY.TRAIN_AHEAD;
                        endAuthorityDistanceM = thisTrainAhead.Value;
                        if (!thisSection.CircuitState.ThisTrainOccupying(this)) thisSection.PreReserve(thisRouted);
                    }
                }

                // check route availability
                // reserve sections which are available

                else
                {
                    int lastValidSectionIndex = 0;
                    bool isAvailable = true;
                    totalLengthM = 0;

                    for (int iindex = 0; iindex <= signalIndex && isAvailable; iindex++)
                    {
                        thisSection = signalRef.TrackCircuitList[newRoute[iindex].TCSectionIndex];

                        if (isAvailable)
                        {
                            if (thisSection.IsAvailable(this))
                            {
                                lastValidSectionIndex = iindex;
                                totalLengthM += (thisSection.Length - offsetM);
                                offsetM = 0;
                                thisSection.Reserve(thisRouted, newRoute);
                            }
                            else
                            {
                                isAvailable = false;
                            }
                        }
                    }

                    // set default authority to max distance
                    endAuthority = END_AUTHORITY.MAX_DISTANCE;
                    endAuthorityDistanceM = totalLengthM;

                    // if last section ends with signal, set authority to signal
                    thisElement = newRoute[lastValidSectionIndex];
                    thisSection = signalRef.TrackCircuitList[thisElement.TCSectionIndex];
                    reqDirection = forward ? thisElement.Direction : (thisElement.Direction == 0 ? 1 : 0);
                    // last section ends with signal
                    if (thisSection.EndSignals[reqDirection] != null)
                    {
                        endAuthority = END_AUTHORITY.SIGNAL;
                        endAuthorityDistanceM = totalLengthM;
                    }

                    // sections not clear - check if end has signal

                    else
                    {

                        TrackCircuitSection nextSection = null;
                        TCRouteElement nextElement = null;

                        if (lastValidSectionIndex < newRoute.Count - 1)
                        {
                            nextElement = newRoute[lastValidSectionIndex + 1];
                            nextSection = signalRef.TrackCircuitList[nextElement.TCSectionIndex];
                        }

                        // check for end authority if not ended with signal
                        // last section is end of track
                        if (thisSection.CircuitType == TrackCircuitSection.TrackCircuitType.EndOfTrack)
                        {
                            endAuthority = END_AUTHORITY.END_OF_TRACK;
                            endAuthorityDistanceM = totalLengthM;
                        }

                        // first non-available section is switch or crossover
                        else if (nextSection != null && (nextSection.CircuitType == TrackCircuitSection.TrackCircuitType.Junction ||
                                     nextSection.CircuitType == TrackCircuitSection.TrackCircuitType.Crossover))
                        {
                            endAuthority = END_AUTHORITY.RESERVED_SWITCH;
                            endAuthorityDistanceM = totalLengthM;
                        }

                        // set authority is end of path unless train ahead
                        else
                        {
                            endAuthority = END_AUTHORITY.END_OF_PATH;
                            endAuthorityDistanceM = totalLengthM;

                            // check if train ahead not moving in opposite direction, in first non-available section

                            if (nextSection != null)
                            {
                                int oppositeDirection = forward ? (nextElement.Direction == 0 ? 1 : 0) : (nextElement.Direction == 0 ? 0 : 1);
                                reqDirection = forward ? nextElement.Direction : (nextElement.Direction == 0 ? 1 : 0);

                                bool oppositeTrain = nextSection.CircuitState.HasTrainsOccupying(oppositeDirection, false);

                                if (!oppositeTrain)
                                {
                                    Dictionary<Train, float> nextTrainInfo = nextSection.TestTrainAhead(this, 0.0f, reqDirection);
                                    if (nextTrainInfo.Count > 0)
                                    {
                                        foreach (KeyValuePair<Train, float> thisTrainAhead in nextTrainInfo)  // there is only one value
                                        {
                                            endAuthority = END_AUTHORITY.TRAIN_AHEAD;
                                            endAuthorityDistanceM = thisTrainAhead.Value + totalLengthM;
                                            lastValidSectionIndex++;
                                            nextSection.PreReserve(thisRouted);
                                        }
                                    }
                                }
                            }
                        }
                    }

                    // remove invalid sections from route
                    if (lastValidSectionIndex < newRoute.Count - 1)
                    {
                        for (int iindex = newRoute.Count - 1; iindex > lastValidSectionIndex; iindex--)
                        {
                            newRoute.RemoveAt(iindex);
                        }
                    }
                }

                // check if route ends at signal and this is first unclear signal
                // if so, request clear signal

                if (endAuthority == END_AUTHORITY.SIGNAL)
                {
                    TrackCircuitSection lastSection = signalRef.TrackCircuitList[newRoute[newRoute.Count - 1].TCSectionIndex];
                    int lastDirection = newRoute[newRoute.Count - 1].Direction;
                    if (lastSection.EndSignals[lastDirection] != null && lastSection.EndSignals[lastDirection].thisRef == nextUnclearSignalIndex)
                    {
                        float remainingDistance = minCheckDistanceM - endAuthorityDistanceM;
                        SignalObject reqSignal = signalRef.SignalObjects[nextUnclearSignalIndex];
                        newRoute = reqSignal.requestClearSignalExplorer(newRoute, remainingDistance, forward ? routedForward : routedBackward, false, 0);
                    }
                }
            }

            // no valid route could be found
            else
            {
                endAuthority = END_AUTHORITY.NO_PATH_RESERVED;
                endAuthorityDistanceM = 0.0f;
            }

            return (newRoute);
        }

        //================================================================================================//
        /// <summary>
        /// Restore Explorer Mode
        /// </summary>

        public void RestoreExplorerMode()
        {
            // get next signal

            // forward
            NextSignalObject[0] = null;
            for (int iindex = 0; iindex < ValidRoute[0].Count && NextSignalObject[0] == null; iindex++)
            {
                TCRouteElement thisElement = ValidRoute[0][iindex];
                TrackCircuitSection thisSection = signalRef.TrackCircuitList[thisElement.TCSectionIndex];
                NextSignalObject[0] = thisSection.EndSignals[thisElement.Direction];
            }

            // backward
            NextSignalObject[1] = null;
            for (int iindex = 0; iindex < ValidRoute[1].Count && NextSignalObject[1] == null; iindex++)
            {
                TCRouteElement thisElement = ValidRoute[1][iindex];
                TrackCircuitSection thisSection = signalRef.TrackCircuitList[thisElement.TCSectionIndex];
                NextSignalObject[1] = thisSection.EndSignals[thisElement.Direction];
            }
        }


        //================================================================================================//
        //
        // Request signal permission in explorer mode
        //

        public void RequestExplorerSignalPermission(ref TCSubpathRoute selectedRoute, int routeIndex)
        {
            // check route for first signal at danger, from present position

            SignalObject reqSignal = null;
            bool signalFound = false;

            if (ValidRoute[routeIndex] != null)
            {
                for (int iIndex = PresentPosition[routeIndex].RouteListIndex; iIndex <= ValidRoute[routeIndex].Count - 1 && !signalFound; iIndex++)
                {
                    TrackCircuitSection thisSection = signalRef.TrackCircuitList[ValidRoute[routeIndex][iIndex].TCSectionIndex];
                    int direction = ValidRoute[routeIndex][iIndex].Direction;

                    if (thisSection.EndSignals[direction] != null)
                    {
                        reqSignal = thisSection.EndSignals[direction];
                        signalFound = (reqSignal.this_sig_lr(MstsSignalFunction.NORMAL) == MstsSignalAspect.STOP);
                    }
                }
            }

            // if no signal at danger is found - report warning
            if (!signalFound)
            {
                if (Simulator.Confirmer != null && this.TrainType != TRAINTYPE.REMOTE) // As Confirmer may not be created until after a restore.
                    Simulator.Confirmer.Message(ConfirmLevel.Information, Simulator.Catalog.GetString("No signal in train's path"));
                return;
            }

            // signal at danger is found - set PERMISSION REQUESTED, and request clear signal
            // if signal has a route, set PERMISSION REQUESTED, and perform signal update
            reqSignal.hasPermission = SignalObject.Permission.Requested;

            TCPosition tempPos = new TCPosition();

            if (routeIndex == 0)
            {
                PresentPosition[0].CopyTo(ref tempPos);
            }
            else
            {
                PresentPosition[1].CopyTo(ref tempPos);
                tempPos.TCDirection = tempPos.TCDirection == 0 ? 1 : 0;
            }

            TCSubpathRoute newRouteR = CheckExplorerPath(routeIndex, tempPos, ValidRoute[routeIndex], true, ref EndAuthorityType[routeIndex],
                ref DistanceToEndNodeAuthorityM[routeIndex]);
            ValidRoute[routeIndex] = newRouteR;
            Simulator.SoundNotify = reqSignal.hasPermission == SignalObject.Permission.Granted ?
                Event.PermissionGranted :
                Event.PermissionDenied;
        }

        //================================================================================================//
        /// <summary>
        /// Process request to set switch in explorer mode
        /// Request may contain direction or actual node
        /// </summary>

        public bool ProcessRequestExplorerSetSwitch(Direction direction)
        {
            // find first switch in required direction

            TrackCircuitSection reqSwitch = null;
            int routeDirectionIndex = direction == Direction.Forward ? 0 : 1;
            bool switchSet = false;

            for (int iindex = 0; iindex < ValidRoute[routeDirectionIndex].Count && reqSwitch == null; iindex++)
            {
                TrackCircuitSection thisSection = signalRef.TrackCircuitList[ValidRoute[routeDirectionIndex][iindex].TCSectionIndex];
                if (thisSection.CircuitType == TrackCircuitSection.TrackCircuitType.Junction)
                {
                    reqSwitch = thisSection;
                }
            }

            if (reqSwitch == null)
            {
                // search beyond last section for switch using default pins (continue through normal sections only)

                TCRouteElement thisElement = ValidRoute[routeDirectionIndex][ValidRoute[routeDirectionIndex].Count - 1];
                TrackCircuitSection lastSection = signalRef.TrackCircuitList[thisElement.TCSectionIndex];
                int curDirection = thisElement.Direction;
                int nextSectionIndex = thisElement.TCSectionIndex;

                bool validRoute = lastSection.CircuitType == TrackCircuitSection.TrackCircuitType.Normal;

                while (reqSwitch == null && validRoute)
                {
                    if (lastSection.CircuitType == TrackCircuitSection.TrackCircuitType.Crossover)
                    {
                        int outPinIndex = curDirection == 0 ? 1 : 0;
                        if (lastSection.Pins[curDirection, 0].Link == nextSectionIndex)
                        {
                            nextSectionIndex = lastSection.Pins[outPinIndex, 0].Link;
                            curDirection = lastSection.Pins[outPinIndex, 0].Direction;
                        }
                        else if (lastSection.Pins[curDirection, 1].Link == nextSectionIndex)
                        {
                            nextSectionIndex = lastSection.Pins[outPinIndex, 1].Link;
                            curDirection = lastSection.Pins[outPinIndex, 1].Direction;
                        }
                    }
                    else
                    {
                        nextSectionIndex = lastSection.Pins[curDirection, 0].Link;
                        curDirection = lastSection.ActivePins[curDirection, 0].Direction;
                        lastSection = signalRef.TrackCircuitList[nextSectionIndex];
                    }

                    if (lastSection.CircuitType == TrackCircuitSection.TrackCircuitType.Junction)
                    {
                        reqSwitch = lastSection;
                    }
                    else if (lastSection.CircuitType != TrackCircuitSection.TrackCircuitType.Normal)
                    {
                        validRoute = false;
                    }
                }
            }

            if (reqSwitch != null)
            {
                // check if switch is clear
                if (!reqSwitch.CircuitState.HasTrainsOccupying() && reqSwitch.CircuitState.TrainReserved == null && reqSwitch.CircuitState.SignalReserved < 0)
                {
                    reqSwitch.JunctionSetManual = reqSwitch.JunctionLastRoute == 0 ? 1 : 0;
                    signalRef.setSwitch(reqSwitch.OriginalIndex, reqSwitch.JunctionSetManual, reqSwitch);
                    switchSet = true;
                }
                // check if switch reserved by this train - if so, dealign
                else if (reqSwitch.CircuitState.TrainReserved != null && reqSwitch.CircuitState.TrainReserved.Train == this)
                {
                    reqSwitch.deAlignSwitchPins();
                    reqSwitch.JunctionSetManual = reqSwitch.JunctionLastRoute == 0 ? 1 : 0;
                    signalRef.setSwitch(reqSwitch.OriginalIndex, reqSwitch.JunctionSetManual, reqSwitch);
                    switchSet = true;
                }

                if (switchSet)
                    ProcessExplorerSwitch(routeDirectionIndex, reqSwitch, direction);
            }
            else
            {
                if (Simulator.Confirmer != null) // As Confirmer may not be created until after a restore.
                    Simulator.Confirmer.Message(ConfirmLevel.Warning, Simulator.Catalog.GetString("No switch found"));
            }

            return (switchSet);
        }

        public bool ProcessRequestExplorerSetSwitch(int reqSwitchIndex)
        {
            // find switch in route - forward first

            int routeDirectionIndex = -1;
            bool switchFound = false;
            Direction direction = Direction.N;

            for (int iindex = 0; iindex < ValidRoute[0].Count - 1 && !switchFound; iindex++)
            {
                if (ValidRoute[0][iindex].TCSectionIndex == reqSwitchIndex)
                {
                    routeDirectionIndex = 0;
                    direction = Direction.Forward;
                    switchFound = true;
                }
            }

            if (ValidRoute[1] != null)
            {
                for (int iindex = 0; iindex < ValidRoute[1].Count - 1 && !switchFound; iindex++)
                {
                    if (ValidRoute[1][iindex].TCSectionIndex == reqSwitchIndex)
                    {
                        routeDirectionIndex = 1;
                        direction = Direction.Reverse;
                        switchFound = true;
                    }
                }
            }

            if (switchFound)
            {
                TrackCircuitSection reqSwitch = signalRef.TrackCircuitList[reqSwitchIndex];
                ProcessExplorerSwitch(routeDirectionIndex, reqSwitch, direction);
                return (true);
            }

            return (false);
        }

        //================================================================================================//
        /// <summary>
        /// Process switching of explorer switch
        /// </summary>

        public void ProcessExplorerSwitch(int routeDirectionIndex, TrackCircuitSection switchSection, Direction direction)
        {
            //<CSComment> Probably also in singleplayer the logic of multiplayer should be used, but it's unwise to modify it just before a release
            TrainRouted thisRouted = direction == Direction.Reverse ^ !MPManager.IsMultiPlayer() ? routedBackward : routedForward;
            TCSubpathRoute selectedRoute = ValidRoute[routeDirectionIndex];

            // store required position
            int reqSwitchPosition = switchSection.JunctionSetManual;

            // find index of section in present route
            int junctionIndex = selectedRoute.GetRouteIndex(switchSection.Index, 0);
            int lastIndex = junctionIndex - 1; // set previous index as last valid index

            // find first signal from train and before junction
            SignalObject firstSignal = null;
            float coveredLength = 0;

            for (int iindex = 0; iindex < junctionIndex && firstSignal == null; iindex++)
            {
                TCRouteElement thisElement = selectedRoute[iindex];
                TrackCircuitSection thisSection = signalRef.TrackCircuitList[thisElement.TCSectionIndex];
                if (iindex > 0) coveredLength += thisSection.Length; // do not use first section

                int signalDirection = thisElement.Direction == 0 ? 0 : 1;

                if (thisSection.EndSignals[signalDirection] != null &&
                    thisSection.EndSignals[signalDirection].enabledTrain != null &&
                    thisSection.EndSignals[signalDirection].enabledTrain.Train == this)
                {
                    firstSignal = thisSection.EndSignals[signalDirection];
                    lastIndex = iindex;
                }
            }

            // if last first is found : reset signal and further signals, clear route as from signal and request clear signal

            if (firstSignal != null)
            {
                firstSignal.ResetSignal(true);

                // breakdown and clear route

                // checke whether trailing or leading
                //<CSComment> Probably also in singleplayer the logic of multiplayer should be used, but it's unwise to modify it just before a release
                if (switchSection.Pins[0, 0].Link == selectedRoute[lastIndex].TCSectionIndex || !MPManager.IsMultiPlayer())
                // leading, train may still own switch

                {

                    signalRef.BreakDownRouteList(selectedRoute, lastIndex + 1, thisRouted);
                    selectedRoute.RemoveRange(lastIndex + 1, selectedRoute.Count - lastIndex - 1);

                    // restore required position (is cleared by route breakdown)
                    switchSection.JunctionSetManual = reqSwitchPosition;

                    // set switch
                    switchSection.deAlignSwitchPins();
                    signalRef.setSwitch(switchSection.OriginalIndex, switchSection.JunctionSetManual, switchSection);

                    // build new route - use signal request
                    float remLength = minCheckDistanceM - coveredLength;
                    TCSubpathRoute newRoute = firstSignal.requestClearSignalExplorer(selectedRoute, remLength, thisRouted, false, 0);
                    selectedRoute = newRoute;
                }
                else
                {
                    // trailing, train must not own switch any more
                    signalRef.BreakDownRouteList(selectedRoute, junctionIndex, thisRouted);
                    selectedRoute.RemoveRange(junctionIndex, selectedRoute.Count - junctionIndex);

                    // restore required position (is cleared by route breakdown)
                    switchSection.JunctionSetManual = reqSwitchPosition;

                    // set switch
                    switchSection.deAlignSwitchPins();
                    signalRef.setSwitch(switchSection.OriginalIndex, switchSection.JunctionSetManual, switchSection);
                }
            }

            // no signal is found - build route using full update process
            else
            {
                signalRef.BreakDownRouteList(selectedRoute, 0, thisRouted);
                selectedRoute.Clear();
                TrainRoute = signalRef.BuildTempRoute(this, PresentPosition[1].TCSectionIndex, PresentPosition[1].TCOffset,
                    PresentPosition[1].TCDirection, Length, false, true, false);
                UpdateExplorerMode(-1);
            }
        }

        //================================================================================================//
        //
        // Switch to explorer mode
        //

        public void ToggleToExplorerMode()
        {
            if (ControlMode == TRAIN_CONTROL.OUT_OF_CONTROL && LeadLocomotive != null)
                ((MSTSLocomotive)LeadLocomotive).SetEmergency(false);

            // set track occupation (using present route)
            UpdateSectionStateExplorer();

            // breakdown present route - both directions if set

            if (ValidRoute[0] != null)
            {
                int listIndex = PresentPosition[0].RouteListIndex;
                signalRef.BreakDownRouteList(ValidRoute[0], listIndex, routedForward);
                ClearDeadlocks();
            }

            ValidRoute[0] = null;
            LastReservedSection[0] = -1;

            if (ValidRoute[1] != null)
            {
                int listIndex = PresentPosition[1].RouteListIndex;
                signalRef.BreakDownRouteList(ValidRoute[1], listIndex, routedBackward);
            }
            ValidRoute[1] = null;
            LastReservedSection[1] = -1;

            // clear all outstanding actions

            ClearActiveSectionItems();
            requiredActions.RemovePendingAIActionItems(true);

            // clear signal info

            NextSignalObject[0] = null;
            NextSignalObject[1] = null;

            SignalObjectItems.Clear();

            PassedSignalSpeeds.Clear();

            // set explorer mode

            ControlMode = TRAIN_CONTROL.EXPLORER;

            // reset routes and check sections either end of train

            PresentPosition[0].RouteListIndex = -1;
            PresentPosition[1].RouteListIndex = -1;
            PreviousPosition[0].RouteListIndex = -1;

            UpdateExplorerMode(-1);
        }

        //================================================================================================//
        /// <summary>
        /// Update out-of-control mode
        /// </summary>

        public void UpdateOutOfControl()
        {

            // train is at a stand : 
            // clear all occupied blocks
            // clear signal/speedpost list 
            // clear DistanceTravelledActions 
            // clear all previous occupied sections 
            // set sections occupied on which train stands

            // all the above is still TODO
        }

        //================================================================================================//
        /// <summary>
        /// Switch to Auto Signal mode
        /// </summary>

        public virtual void SwitchToSignalControl(SignalObject thisSignal)
        {
            // in auto mode, use forward direction only

            ControlMode = TRAIN_CONTROL.AUTO_SIGNAL;
            thisSignal.requestClearSignal(ValidRoute[0], routedForward, 0, false, null);

            // enable any none-NORMAL signals between front of train and first NORMAL signal
            int firstSectionIndex = PresentPosition[0].RouteListIndex;
            int lastSectionIndex = ValidRoute[0].GetRouteIndex(thisSignal.TCReference, firstSectionIndex);

            // first, all signals in present section beyond position of train
            TrackCircuitSection thisSection = signalRef.TrackCircuitList[ValidRoute[0][firstSectionIndex].TCSectionIndex];
            int thisDirection = ValidRoute[0][firstSectionIndex].Direction;

            for (int isigtype = 0; isigtype < signalRef.ORTSSignalTypeCount; isigtype++)
            {
                TrackCircuitSignalList thisList = thisSection.CircuitItems.TrackCircuitSignals[thisDirection][isigtype];
                foreach (TrackCircuitSignalItem thisItem in thisList.TrackCircuitItem)
                {
                    if (thisItem.SignalLocation > PresentPosition[0].TCOffset && !thisItem.SignalRef.isSignalNormal())
                    {
                        thisItem.SignalRef.enabledTrain = this.routedForward;
                    }
                }
            }

            // next, signals in any further sections
            for (int iSectionIndex = firstSectionIndex + 1; iSectionIndex <= lastSectionIndex; iSectionIndex++)
            {
                thisSection = signalRef.TrackCircuitList[ValidRoute[0][firstSectionIndex].TCSectionIndex];
                thisDirection = ValidRoute[0][firstSectionIndex].Direction;

                for (int isigtype = 0; isigtype < signalRef.ORTSSignalTypeCount; isigtype++)
                {
                    TrackCircuitSignalList thisList = thisSection.CircuitItems.TrackCircuitSignals[thisDirection][isigtype];
                    foreach (TrackCircuitSignalItem thisItem in thisList.TrackCircuitItem)
                    {
                        if (!thisItem.SignalRef.isSignalNormal())
                        {
                            thisItem.SignalRef.enabledTrain = this.routedForward;
                        }
                    }
                }
            }
        }

        //================================================================================================//
        /// <summary>
        /// Switch to Auto Node mode
        /// </summary>

        public virtual void SwitchToNodeControl(int thisSectionIndex)
        {
            // reset enabled signal if required
            if (ControlMode == TRAIN_CONTROL.AUTO_SIGNAL && NextSignalObject[0] != null && NextSignalObject[0].enabledTrain == routedForward)
            {
                // reset any claims
                foreach (TCRouteElement thisElement in NextSignalObject[0].signalRoute)
                {
                    TrackCircuitSection thisSection = signalRef.TrackCircuitList[thisElement.TCSectionIndex];
                    if (thisSection.CircuitState.TrainClaimed.ContainsTrain(routedForward))
                    {
                        thisSection.UnclaimTrain(routedForward);
                    }
                }

                // reset signal
                NextSignalObject[0].enabledTrain = null;
                NextSignalObject[0].ResetSignal(true);
            }

            // use direction forward only
            float maxDistance = Math.Max(AllowedMaxSpeedMpS * maxTimeS, minCheckDistanceM);
            float clearedDistanceM = 0.0f;

            int activeSectionIndex = thisSectionIndex;
            int endListIndex = -1;

            ControlMode = TRAIN_CONTROL.AUTO_NODE;
            EndAuthorityType[0] = END_AUTHORITY.NO_PATH_RESERVED;
            IndexNextSignal = -1; // no next signal in Node Control

            // if section is set, check if it is on route and ahead of train

            if (activeSectionIndex > 0)
            {
                endListIndex = ValidRoute[0].GetRouteIndex(thisSectionIndex, PresentPosition[0].RouteListIndex);

                // section is not on route - give warning and break down route, following active links and resetting reservation

                if (endListIndex < 0)
                {
                    signalRef.BreakDownRoute(thisSectionIndex, routedForward);
                    activeSectionIndex = -1;
                }

                // if section is (still) set, check if this is at maximum distance

                if (activeSectionIndex > 0)
                {
                    TrackCircuitSection thisSection = signalRef.TrackCircuitList[activeSectionIndex];
                    clearedDistanceM = GetDistanceToTrain(activeSectionIndex, thisSection.Length);

                    if (clearedDistanceM > maxDistance)
                    {
                        EndAuthorityType[0] = END_AUTHORITY.MAX_DISTANCE;
                        LastReservedSection[0] = thisSection.Index;
                        DistanceToEndNodeAuthorityM[0] = clearedDistanceM;
                    }
                }
                else
                {
                    EndAuthorityType[0] = END_AUTHORITY.NO_PATH_RESERVED;
                }
            }

            // new request or not beyond max distance

            if (activeSectionIndex < 0 || EndAuthorityType[0] != END_AUTHORITY.MAX_DISTANCE)
            {
                signalRef.requestClearNode(routedForward, ValidRoute[0]);
            }
        }

        //================================================================================================//
        //
        // Request to switch to or from manual mode
        //

        public void RequestToggleManualMode()
        {
            if (TrainType == TRAINTYPE.AI_PLAYERHOSTING)
            {
                if (Simulator.Confirmer != null) // As Confirmer may not be created until after a restore.
                    Simulator.Confirmer.Message(ConfirmLevel.Warning, Simulator.Catalog.GetString("You cannot enter manual mode when autopiloted"));
            }
            else if (IsPathless && ControlMode != TRAIN_CONTROL.OUT_OF_CONTROL && ControlMode == TRAIN_CONTROL.MANUAL)
            {
                if (Simulator.Confirmer != null) // As Confirmer may not be created until after a restore.
                    Simulator.Confirmer.Message(ConfirmLevel.Warning, Simulator.Catalog.GetString("You cannot use this command for pathless trains"));
            }
            else if (ControlMode == TRAIN_CONTROL.MANUAL)
            {
                // check if train is back on path

                TCSubpathRoute lastRoute = TCRoute.TCRouteSubpaths[TCRoute.activeSubpath];
                int routeIndex = lastRoute.GetRouteIndex(PresentPosition[0].TCSectionIndex, 0);

                if (routeIndex < 0)
                {
                    if (Simulator.Confirmer != null) // As Confirmer may not be created until after a restore.
                        Simulator.Confirmer.Message(ConfirmLevel.Warning, Simulator.Catalog.GetString("Train is not back on original route"));
                }
                else
                {
                    int lastDirection = lastRoute[routeIndex].Direction;
                    int presentDirection = PresentPosition[0].TCDirection;
                    if (lastDirection != presentDirection && Math.Abs(SpeedMpS) > 0.1f)
                    {
                        if (Simulator.Confirmer != null) // As Confirmer may not be created until after a restore.
                            Simulator.Confirmer.Message(ConfirmLevel.Warning, Simulator.Catalog.GetString("Original route is reverse from present direction, stop train before switching"));
                    }
                    else
                    {
                        ToggleFromManualMode(routeIndex);
                        Simulator.Confirmer.Confirm(CabControl.SignalMode, CabSetting.On);
                    }
                }

            }
            else if (ControlMode == TRAIN_CONTROL.EXPLORER)
            {
                if (LeadLocomotive != null &&
                    (((MSTSLocomotive)LeadLocomotive).TrainBrakeController.TCSEmergencyBraking || ((MSTSLocomotive)LeadLocomotive).TrainBrakeController.TCSFullServiceBraking))
                {
                    ((MSTSLocomotive)LeadLocomotive).SetEmergency(false);
                    ResetExplorerMode();
                    return;
                }
                else if (Simulator.Confirmer != null) // As Confirmer may not be created until after a restore.
                    Simulator.Confirmer.Message(ConfirmLevel.Warning, Simulator.Catalog.GetString("Cannot change to Manual Mode while in Explorer Mode"));
            }
            else
            {
                ToggleToManualMode();
                Simulator.Confirmer.Confirm(CabControl.SignalMode, CabSetting.Off);
            }
        }

        //================================================================================================//
        //
        // Switch to manual mode
        //

        public void ToggleToManualMode()
        {
            if (LeadLocomotive != null)
                ((MSTSLocomotive)LeadLocomotive).SetEmergency(false);

            // set track occupation (using present route)
            UpdateSectionStateManual();

            // breakdown present route - both directions if set

            if (ValidRoute[0] != null)
            {
                int listIndex = PresentPosition[0].RouteListIndex;
                signalRef.BreakDownRouteList(ValidRoute[0], listIndex, routedForward);
                ClearDeadlocks();
            }

            ValidRoute[0] = null;
            LastReservedSection[0] = -1;

            if (ValidRoute[1] != null)
            {
                int listIndex = PresentPosition[1].RouteListIndex;
                signalRef.BreakDownRouteList(ValidRoute[1], listIndex, routedBackward);
            }
            ValidRoute[1] = null;
            LastReservedSection[1] = -1;

            // clear all outstanding actions

            ClearActiveSectionItems();
            requiredActions.RemovePendingAIActionItems(true);

            // clear signal info

            NextSignalObject[0] = null;
            NextSignalObject[1] = null;

            SignalObjectItems.Clear();

            PassedSignalSpeeds.Clear();

            // set manual mode

            ControlMode = TRAIN_CONTROL.MANUAL;

            // reset routes and check sections either end of train

            PresentPosition[0].RouteListIndex = -1;
            PresentPosition[1].RouteListIndex = -1;
            PreviousPosition[0].RouteListIndex = -1;

            UpdateManualMode(-1);
        }

        //================================================================================================//
        //
        // Switch back from manual mode
        //

        public void ToggleFromManualMode(int routeIndex)
        {
            // extract route at present front position

            TCSubpathRoute newRoute = new TCSubpathRoute();
            TCSubpathRoute oldRoute = TCRoute.TCRouteSubpaths[TCRoute.activeSubpath];

            // test on reversal, if so check rear of train

            bool reversal = false;
            if (!CheckReversal(oldRoute, ref reversal))
            {
                if (Simulator.Confirmer != null) // As Confirmer may not be created until after a restore.
                    Simulator.Confirmer.Message(ConfirmLevel.Warning, Simulator.Catalog.GetString("Reversal required and rear of train not on required route"));
                return;
            }

            // breakdown present routes, forward and backward
            signalRef.BreakDownRouteList(ValidRoute[0], 0, routedForward);
            signalRef.BreakDownRouteList(ValidRoute[1], 0, routedBackward);


            // clear occupied sections

            for (int iSection = OccupiedTrack.Count - 1; iSection >= 0; iSection--)
            {
                TrackCircuitSection thisSection = OccupiedTrack[iSection];
                thisSection.ResetOccupied(this);
            }

            // remove any actions build up during manual mode
            requiredActions.RemovePendingAIActionItems(true);

            // restore train placement
            RestoreTrainPlacement(ref newRoute, oldRoute, routeIndex, reversal);

            // restore distance travelled in Present Position
            PresentPosition[0].DistanceTravelledM = DistanceTravelledM;
            PresentPosition[1].DistanceTravelledM = DistanceTravelledM - Length;

            // set track occupation (using present route)
            // This procedure is also needed for clearing track occupation.
            UpdateSectionStateManual();

            // restore signal information
            PassedSignalSpeeds.Clear();
            InitializeSignals(true);

            // restore deadlock information

            CheckDeadlock(ValidRoute[0], Number);    // Check deadlock against all other trains

            // switch to AutoNode mode

            LastReservedSection[0] = PresentPosition[0].TCSectionIndex;
            LastReservedSection[1] = PresentPosition[1].TCSectionIndex;

            if (!Simulator.TimetableMode) AuxActionsContain.ResetAuxAction(this);
            SwitchToNodeControl(PresentPosition[0].TCSectionIndex);
            TCRoute.SetReversalOffset(Length, Simulator.TimetableMode);
        }

        //================================================================================================//
        //
        // ResetExplorerMode
        //

        public void ResetExplorerMode()
        {
            if (ControlMode == TRAIN_CONTROL.OUT_OF_CONTROL && LeadLocomotive != null)
                ((MSTSLocomotive)LeadLocomotive).SetEmergency(false);

            // set track occupation (using present route)
            UpdateSectionStateExplorer();

            // breakdown present route - both directions if set

            if (ValidRoute[0] != null)
            {
                int listIndex = PresentPosition[0].RouteListIndex;
                signalRef.BreakDownRouteList(ValidRoute[0], listIndex, routedForward);
                ClearDeadlocks();
            }

            ValidRoute[0] = null;
            LastReservedSection[0] = -1;

            if (ValidRoute[1] != null)
            {
                int listIndex = PresentPosition[1].RouteListIndex;
                signalRef.BreakDownRouteList(ValidRoute[1], listIndex, routedBackward);
            }
            ValidRoute[1] = null;
            LastReservedSection[1] = -1;

            // clear all outstanding actions

            ClearActiveSectionItems();
            requiredActions.RemovePendingAIActionItems(true);

            // clear signal info

            NextSignalObject[0] = null;
            NextSignalObject[1] = null;

            SignalObjectItems.Clear();

            PassedSignalSpeeds.Clear();

            // set explorer mode

            ControlMode = TRAIN_CONTROL.EXPLORER;

            // reset routes and check sections either end of train

            PresentPosition[0].RouteListIndex = -1;
            PresentPosition[1].RouteListIndex = -1;
            PreviousPosition[0].RouteListIndex = -1;

            UpdateExplorerMode(-1);
        }

        //================================================================================================//
        //
        // Check if reversal is required
        //

        public bool CheckReversal(TCSubpathRoute reqRoute, ref bool reversal)
        {
            bool valid = true;

            int presentRouteIndex = ValidRoute[0].GetRouteIndex(PresentPosition[0].TCSectionIndex, 0);
            int reqRouteIndex = reqRoute.GetRouteIndex(PresentPosition[0].TCSectionIndex, 0);
            if (presentRouteIndex < 0 || reqRouteIndex < 0)
            {
                valid = false;  // front of train not on present route or not on required route
            }
            // valid point : check if reversal is required
            else
            {
                TCRouteElement presentElement = ValidRoute[0][presentRouteIndex];
                TCRouteElement pathElement = reqRoute[reqRouteIndex];

                if (presentElement.Direction != pathElement.Direction)
                {
                    reversal = true;
                }
            }

            // if reversal required : check if rear of train is on required route
            if (valid && reversal)
            {
                int rearRouteIndex = reqRoute.GetRouteIndex(PresentPosition[1].TCSectionIndex, 0);
                valid = rearRouteIndex >= 0;
            }

            return (valid);
        }

        //================================================================================================//
        //
        // Restore train placement
        //

        public void RestoreTrainPlacement(ref TCSubpathRoute newRoute, TCSubpathRoute oldRoute, int frontIndex, bool reversal)
        {
            // reverse train if required

            if (reversal)
            {
                ReverseFormation(true);
                // active subpath must be incremented in parallel in incorporated train if present
                if (IncorporatedTrainNo >= 0) IncrementSubpath(Simulator.TrainDictionary[IncorporatedTrainNo]);
            }

            // reset distance travelled

            DistanceTravelledM = 0.0f;

            // check if end of train on original route
            // copy sections from earliest start point (front or rear)

            int rearIndex = oldRoute.GetRouteIndex(PresentPosition[1].TCSectionIndex, 0);
            int startIndex = rearIndex >= 0 ? Math.Min(rearIndex, frontIndex) : frontIndex;

            for (int iindex = startIndex; iindex < oldRoute.Count; iindex++)
            {
                newRoute.Add(oldRoute[iindex]);
            }

            // if rear not on route, build route under train and add sections

            if (rearIndex < 0)
            {

                TCSubpathRoute tempRoute = signalRef.BuildTempRoute(this, PresentPosition[1].TCSectionIndex, PresentPosition[1].TCOffset,
                            PresentPosition[1].TCDirection, Length, true, true, false);

                for (int iindex = tempRoute.Count - 1; iindex >= 0; iindex--)
                {
                    TCRouteElement thisElement = tempRoute[iindex];
                    if (!newRoute.ContainsSection(thisElement))
                    {
                        newRoute.Insert(0, thisElement);
                    }
                }
            }

            // set route as valid route

            ValidRoute[0] = newRoute;

            // Reindexes ReversalInfo items
            if (TCRoute.ReversalInfo[TCRoute.activeSubpath].LastDivergeIndex >= 0)
                TCRoute.ReversalInfo[TCRoute.activeSubpath].LastDivergeIndex = ValidRoute[0].GetRouteIndex(TCRoute.ReversalInfo[TCRoute.activeSubpath].DivergeSectorIndex, 0);
            if (TCRoute.ReversalInfo[TCRoute.activeSubpath].LastSignalIndex >= 0)
                TCRoute.ReversalInfo[TCRoute.activeSubpath].LastSignalIndex = ValidRoute[0].GetRouteIndex(TCRoute.ReversalInfo[TCRoute.activeSubpath].SignalSectorIndex, 0);



            // get index of first section in route

            rearIndex = ValidRoute[0].GetRouteIndex(PresentPosition[1].TCSectionIndex, 0);
            PresentPosition[1].RouteListIndex = rearIndex;

            // get index of front of train

            frontIndex = ValidRoute[0].GetRouteIndex(PresentPosition[0].TCSectionIndex, 0);
            PresentPosition[0].RouteListIndex = frontIndex;

            PresentPosition[0].CopyTo(ref PreviousPosition[0]);

            // set track occupied - forward only

            foreach (TrackCircuitSection thisSection in OccupiedTrack)
            {
                if (!thisSection.CircuitState.ThisTrainOccupying(this))
                {
                    thisSection.Reserve(routedForward, ValidRoute[0]);
                    thisSection.SetOccupied(routedForward);
                }
            }

        }


        //================================================================================================//
        //
        // Request permission to pass signal
        //

        public void RequestSignalPermission(Direction direction)
        {
            if (MPManager.IsClient())
            {
                MPManager.Notify((new MSGResetSignal(MPManager.GetUserName())).ToString());
                return;
            }
            if (ControlMode == TRAIN_CONTROL.MANUAL)
            {
                if (direction == Direction.Forward)
                {
                    RequestManualSignalPermission(ref ValidRoute[0], 0);
                }
                else
                {
                    RequestManualSignalPermission(ref ValidRoute[1], 1);
                }
            }
            else if (ControlMode == TRAIN_CONTROL.EXPLORER)
            {
                if (direction == Direction.Forward)
                {
                    RequestExplorerSignalPermission(ref ValidRoute[0], 0);
                }
                else
                {
                    RequestExplorerSignalPermission(ref ValidRoute[1], 1);
                }
            }
            else
            {
                if (direction != Direction.Forward)
                {
                    if (Simulator.Confirmer != null) // As Confirmer may not be created until after a restore.
                        Simulator.Confirmer.Message(ConfirmLevel.Warning, Simulator.Catalog.GetString("Cannot clear signal behind train while in AUTO mode"));
                    Simulator.SoundNotify = Event.PermissionDenied;
                }

                else if (NextSignalObject[0] != null)
                {
                    NextSignalObject[0].hasPermission = SignalObject.Permission.Requested;
                }
            }
        }

        //================================================================================================//
        //
        // Request reset signal
        //

        public void RequestResetSignal(Direction direction)
        {
            if (!MPManager.IsMultiPlayer())
            {
                if (ControlMode == TRAIN_CONTROL.MANUAL || ControlMode == TRAIN_CONTROL.EXPLORER)
                {
                    int reqRouteIndex = direction == Direction.Forward ? 0 : 1;

                    if (NextSignalObject[reqRouteIndex] != null &&
                        NextSignalObject[reqRouteIndex].this_sig_lr(MstsSignalFunction.NORMAL) != MstsSignalAspect.STOP)
                    {
                        int routeIndex = ValidRoute[reqRouteIndex].GetRouteIndex(NextSignalObject[reqRouteIndex].TCNextTC, PresentPosition[reqRouteIndex].RouteListIndex);
                        signalRef.BreakDownRouteList(ValidRoute[reqRouteIndex], routeIndex, routedForward);
                        ValidRoute[reqRouteIndex].RemoveRange(routeIndex, ValidRoute[reqRouteIndex].Count - routeIndex);

                        NextSignalObject[reqRouteIndex].ResetSignal(true);
                    }
                }
            }
        }

        //================================================================================================//
        /// <summary>
        /// Get distance from train to object position using route list
        /// </summary>

        public float GetObjectDistanceToTrain(ObjectItemInfo thisObject)
        {

            // follow active links to get to object

            int reqSectionIndex = thisObject.ObjectDetails.TCReference;
            float endOffset = thisObject.ObjectDetails.TCOffset;

            float distanceM = GetDistanceToTrain(reqSectionIndex, endOffset);

            //          if (distanceM < 0)
            //          {
            //              distanceM = thisObject.ObjectDetails.DistanceTo(FrontTDBTraveller);
            //          }

            return (distanceM);
        }

        //================================================================================================//
        /// <summary>
        /// Get distance from train to location using route list
        /// TODO : rewrite to use active links, and if fails use traveller
        /// location must have same direction as train
        /// </summary>

        public float GetDistanceToTrain(int sectionIndex, float endOffset)
        {
            // use start of list to see if passed position

            int endListIndex = ValidRoute[0].GetRouteIndex(sectionIndex, PresentPosition[0].RouteListIndex);
            if (endListIndex < 0)
                endListIndex = ValidRoute[0].GetRouteIndex(sectionIndex, 0);

            if (endListIndex >= 0 && endListIndex < PresentPosition[0].RouteListIndex) // index before present so we must have passed object
            {
                return (-1.0f);
            }

            if (endListIndex == PresentPosition[0].RouteListIndex && endOffset < PresentPosition[0].TCOffset) // just passed
            {
                return (-1.0f);
            }

            // section is not on route

            if (endListIndex < 0)
            {
                return (-1.0f);
            }

            int thisSectionIndex = PresentPosition[0].TCSectionIndex;
            int direction = PresentPosition[0].TCDirection;
            float startOffset = PresentPosition[0].TCOffset;
            TrackCircuitSection thisSection = signalRef.TrackCircuitList[thisSectionIndex];

            return (thisSection.GetDistanceBetweenObjects(thisSectionIndex, startOffset, direction, sectionIndex, endOffset));
        }

        //================================================================================================//
        /// <summary>
        /// Switch train to Out-of-Control
        /// Set mode and apply emergency brake
        /// </summary>

        public void SetTrainOutOfControl(OUTOFCONTROL reason)
        {

            if (ControlMode == TRAIN_CONTROL.OUT_OF_CONTROL) // allready out of control, so exit
            {
                return;
            }

            // clear all reserved sections etc. - both directions
            if (ControlMode == TRAIN_CONTROL.AUTO_SIGNAL)
            {
                if (NextSignalObject[0] != null && NextSignalObject[0].enabledTrain == routedForward)
                {
                    var routeIndexBeforeSignal = NextSignalObject[0].thisTrainRouteIndex - 1;
                    NextSignalObject[0].ResetSignal(true);
                    if (routeIndexBeforeSignal >= 0)
                        signalRef.BreakDownRoute(ValidRoute[0][routeIndexBeforeSignal].TCSectionIndex, routedForward);
                }
                if (NextSignalObject[1] != null && NextSignalObject[1].enabledTrain == routedBackward)
                {
                    NextSignalObject[1].ResetSignal(true);
                }
            }
            else if (ControlMode == TRAIN_CONTROL.AUTO_NODE)
            {
                signalRef.BreakDownRoute(LastReservedSection[0], routedForward);
            }

            // TODO : clear routes for MANUAL
            if (!MPManager.IsMultiPlayer() || Simulator.TimetableMode || reason != OUTOFCONTROL.OUT_OF_PATH || IsActualPlayerTrain)
            {

                // set control state and issue warning

                if (ControlMode != TRAIN_CONTROL.EXPLORER)
                    ControlMode = TRAIN_CONTROL.OUT_OF_CONTROL;

                var report = string.Format("Train {0} is out of control and will be stopped. Reason : ", Number.ToString());

                OutOfControlReason = reason;

                switch (reason)
                {
                    case (OUTOFCONTROL.SPAD):
                        report = String.Concat(report, " train passed signal at Danger");
                        break;
                    case (OUTOFCONTROL.SPAD_REAR):
                        report = String.Concat(report, " train passed signal at Danger at rear of train");
                        break;
                    case (OUTOFCONTROL.OUT_OF_AUTHORITY):
                        report = String.Concat(report, " train passed limit of authority");
                        break;
                    case (OUTOFCONTROL.OUT_OF_PATH):
                        report = String.Concat(report, " train has ran off its allocated path");
                        break;
                    case (OUTOFCONTROL.SLIPPED_INTO_PATH):
                        report = String.Concat(report, " train slipped back into path of another train");
                        break;
                    case (OUTOFCONTROL.SLIPPED_TO_ENDOFTRACK):
                        report = String.Concat(report, " train slipped of the end of track");
                        break;
                    case (OUTOFCONTROL.OUT_OF_TRACK):
                        report = String.Concat(report, " train has moved off the track");
                        break;
                }

#if DEBUG_REPORTS
                File.AppendAllText(@"C:\temp\printproc.txt", report + "\n");
#endif
                if (CheckTrain)
                {
                    File.AppendAllText(@"C:\temp\checktrain.txt", report + "\n");
                }

                if (LeadLocomotive != null)
                    ((MSTSLocomotive)LeadLocomotive).SetEmergency(true);
            }
            // the AI train is now out of path. Instead of killing him, we give him a chance on a new path
            else
            {
                GenerateValidRoute(PresentPosition[0].RouteListIndex, PresentPosition[0].TCSectionIndex);
                // switch to NODE mode
                if (ControlMode == TRAIN_CONTROL.AUTO_SIGNAL)
                {
                    SwitchToNodeControl(PresentPosition[0].TCSectionIndex);
                }
                // reset actions to recalculate distances
                if (TrainType == TRAINTYPE.AI || TrainType == TRAINTYPE.AI_PLAYERHOSTING) ((AITrain)this).ResetActions(true);
            }
        }

        //================================================================================================//
        /// <summary>
        /// Re-routes a train in auto mode after a switch moved manually
        /// </summary>

        public void ReRouteTrain(int forcedRouteSectionIndex, int forcedTCSectionIndex)
        {
            // check for any stations in abandoned path
            if (ControlMode == TRAIN_CONTROL.AUTO_SIGNAL || ControlMode == TRAIN_CONTROL.AUTO_NODE)
                // Local trains, having a defined TCRoute
            {
                int actSubpath = TCRoute.activeSubpath;
                Dictionary<int, StationStop> abdStations = new Dictionary<int, StationStop>();

                CheckAbandonedStations(forcedRouteSectionIndex, ValidRoute[0].Count - 1, actSubpath, abdStations);
                ResetValidRoute();
                GenerateValidRoute(forcedRouteSectionIndex, forcedTCSectionIndex);
                // check for abandoned stations - try to find alternative on passing path
                LookForReplacementStations(abdStations, ValidRoute[0], ValidRoute[0]);
            }
        }

        //================================================================================================//
        /// <summary>
        /// Resets ValidRoute after some event like a switch moved
        /// </summary>

        public void ResetValidRoute()
        {
            // clear all reserved sections etc. - both directions
            if (ControlMode == TRAIN_CONTROL.AUTO_SIGNAL)
            {
                if (NextSignalObject[0] != null && NextSignalObject[0].enabledTrain == routedForward)
                {
                    int routeIndexBeforeSignal = NextSignalObject[0].thisTrainRouteIndex - 1;
                    NextSignalObject[0].ResetSignal(true);
                    if (routeIndexBeforeSignal >= 0)
                        signalRef.BreakDownRoute(ValidRoute[0][routeIndexBeforeSignal].TCSectionIndex, routedForward);
                }
                if (NextSignalObject[1] != null && NextSignalObject[1].enabledTrain == routedBackward)
                {
                    NextSignalObject[1].ResetSignal(true);
                }
            }
            else if (ControlMode == TRAIN_CONTROL.AUTO_NODE)
            {
                signalRef.BreakDownRoute(LastReservedSection[0], routedForward);
            }
        }


        //================================================================================================//
        /// <summary>
        /// Generates a new ValidRoute after some event like a switch moved
        /// </summary>

        public void GenerateValidRoute(int forcedRouteSectionIndex, int forcedTCSectionIndex)
        {
            // We don't kill the AI train and build a new route for it
            // first of all we have to find out the new route
            List<int> tempSections = new List<int>();
            if (TCRoute.OriginalSubpath == -1) TCRoute.OriginalSubpath = TCRoute.activeSubpath;
            if (PresentPosition[0].RouteListIndex > 0)
                // clean case, train is in route and switch has been forced in front of it
                tempSections = signalRef.ScanRoute(this, forcedTCSectionIndex, 0, ValidRoute[0][forcedRouteSectionIndex].Direction,
                        true, 0, true, true,
                        false, false, true, false, false, false, false, IsFreight, false, true);
            else
                // dirty case, train is out of route and has already passed forced switch
                tempSections = signalRef.ScanRoute(this, PresentPosition[0].TCSectionIndex, PresentPosition[0].TCOffset,
                    PresentPosition[0].TCDirection, true, 0, true, true,
                    false, false, true, false, false, false, false, IsFreight, false, true);

            TCSubpathRoute newRoute = new TCSubpathRoute();
            // Copy part of route already run
            if (PresentPosition[0].RouteListIndex > 0)
            {
                for (int routeListIndex = 0; routeListIndex < forcedRouteSectionIndex; routeListIndex++) newRoute.Add(ValidRoute[0][routeListIndex]);
            }
            else if (PresentPosition[0].RouteListIndex < 0)
            {
                for (int routeListIndex = 0; routeListIndex <= PreviousPosition[0].RouteListIndex + 1; routeListIndex++) newRoute.Add(ValidRoute[0][routeListIndex]); // maybe + 1 is wrong?
            }
            if (tempSections.Count > 0)
            {
                // Add new part of route
                TCRouteElement thisElement = null;
                int prevSection = -2;    // preset to invalid
                var tempSectionsIndex = 0;
                foreach (int sectionIndex in tempSections)
                {
                    int sectionDirection = sectionIndex > 0 ? 0 : 1;
                    thisElement = new TCRouteElement(signalRef.TrackCircuitList[Math.Abs(sectionIndex)],
                            sectionDirection, signalRef, prevSection);
                    // if junction, you have to adjust the OutPin
                    signalRef.TrackCircuitList[Math.Abs(sectionIndex)].CircuitState.Forced = false;
                    if (signalRef.TrackCircuitList[Math.Abs(sectionIndex)].CircuitType == TrackCircuitSection.TrackCircuitType.Junction && thisElement.FacingPoint == true)
                    {
                        var TCSection = signalRef.TrackCircuitList[Math.Abs(sectionIndex)];
                        if (tempSectionsIndex < tempSections.Count - 1 && TCSection.Pins[sectionDirection, 1].Link == tempSections[tempSectionsIndex + 1])
                            thisElement.OutPin[1] = 1;
                        else thisElement.OutPin[1] = 0;
                    }
                    newRoute.Add(thisElement);
                    prevSection = Math.Abs(sectionIndex);
                    tempSectionsIndex++;
                }

                // Check if we are returning to original route
                int lastAlternativeSectionIndex = TCRoute.TCRouteSubpaths[TCRoute.OriginalSubpath].GetRouteIndex(newRoute[newRoute.Count - 1].TCSectionIndex, 0);
                if (lastAlternativeSectionIndex != -1)
                {
                    // continued path
                    var thisRoute = TCRoute.TCRouteSubpaths[TCRoute.OriginalSubpath];
                    for (int iElement = lastAlternativeSectionIndex + 1; iElement < thisRoute.Count; iElement++)
                    {
                        newRoute.Add(thisRoute[iElement]);
                    }

                    if (TCRoute.activeSubpath != TCRoute.OriginalSubpath)
                    {
                        TCRoute.TCRouteSubpaths[TCRoute.activeSubpath] = null;
                        TCRoute.ReversalInfo[TCRoute.activeSubpath] = null;
                        TCRoute.LoopEnd.RemoveAt(TCRoute.activeSubpath);
                    }
                    TCRoute.activeSubpath = TCRoute.OriginalSubpath;
                    TCRoute.OriginalSubpath = - 1;

                    // readjust item indexes
                    // Reindexes ReversalInfo items
                    var countDifference = newRoute.Count - ValidRoute[0].Count;
                    if (TCRoute.ReversalInfo[TCRoute.activeSubpath].LastDivergeIndex >= 0)
                        TCRoute.ReversalInfo[TCRoute.activeSubpath].LastDivergeIndex = TCRoute.ReversalInfo[TCRoute.activeSubpath].LastDivergeIndex + countDifference;
                    if (TCRoute.ReversalInfo[TCRoute.activeSubpath].LastSignalIndex >= 0)
                        TCRoute.ReversalInfo[TCRoute.activeSubpath].LastSignalIndex = TCRoute.ReversalInfo[TCRoute.activeSubpath].LastSignalIndex + countDifference;

                    TCRoute.TCRouteSubpaths[TCRoute.activeSubpath] = newRoute;

                }
                else
                {
                    // put at the end of the subpath list the new route
                    TCRoute.TCRouteSubpaths.Add(newRoute);

                    // TODO add reversalInfo here.
                    TCRoute.activeSubpath = TCRoute.TCRouteSubpaths.Count - 1;

                    TCRoute.ReversalInfo.Add(new TCReversalInfo());
                    TCRoute.ReversalInfo[TCRoute.ReversalInfo.Count - 1].ReversalIndex = newRoute.Count - 1;
                    TCRoute.ReversalInfo[TCRoute.ReversalInfo.Count - 1].ReversalSectionIndex = newRoute[newRoute.Count - 1].TCSectionIndex;
                    TrackCircuitSection endSection = signalRef.TrackCircuitList[newRoute[newRoute.Count - 1].TCSectionIndex];
                    TCRoute.ReversalInfo[TCRoute.ReversalInfo.Count - 1].ReverseReversalOffset = endSection.Length;
                    TCRoute.LoopEnd.Add(-1);
                }
            }
            // then we pass this route to ValidRoute[0]
            ValidRoute[0] = newRoute;
            // we set the routelistindex of the present position in case it was = -1
            if (PresentPosition[0].RouteListIndex == -1)
                PresentPosition[0].RouteListIndex = ValidRoute[0].GetRouteIndex(PresentPosition[0].TCSectionIndex, PreviousPosition[0].RouteListIndex);

            // reset signal information

            SignalObjectItems.Clear();
            NextSignalObject[0] = null;
            // create new list
            InitializeSignals(true);
            LastReservedSection[0] = PresentPosition[0].TCSectionIndex;
            CheckDeadlock(ValidRoute[0], Number);    // Check deadlock against all other trains
        }

        //================================================================================================//
        /// <summary>
        /// Perform actions linked to distance travelled
        /// </summary>

        public virtual void PerformActions(List<DistanceTravelledItem> nowActions)
        {
            foreach (var thisAction in nowActions)
            {
                if (thisAction is ClearSectionItem)
                {
                    ClearOccupiedSection(thisAction as ClearSectionItem);
                }
                else if (thisAction is ActivateSpeedLimit)
                {
                    SetPendingSpeedLimit(thisAction as ActivateSpeedLimit);
                }
                else if (thisAction is AuxActionItem)
                {
                    int presentTime = Convert.ToInt32(Math.Floor(Simulator.ClockTime));
                    ((AuxActionItem)thisAction).ProcessAction(this, presentTime);
                }
            }
        }

        //================================================================================================//
        /// <summary>
        /// Clear section
        /// </summary>

        public void ClearOccupiedSection(ClearSectionItem sectionInfo)
        {
            int thisSectionIndex = sectionInfo.TrackSectionIndex;
            TrackCircuitSection thisSection = signalRef.TrackCircuitList[thisSectionIndex];

            thisSection.ClearOccupied(this, true);
        }

        //================================================================================================//
        /// <summary>
        /// Set pending speed limits
        /// </summary>

        public void SetPendingSpeedLimit(ActivateSpeedLimit speedInfo)
        {
            float prevMaxSpeedMpS = AllowedMaxSpeedMpS;

            if (speedInfo.MaxSpeedMpSSignal > 0)
            {
                allowedMaxSpeedSignalMpS = Simulator.TimetableMode ? speedInfo.MaxSpeedMpSSignal : allowedAbsoluteMaxSpeedSignalMpS;
                AllowedMaxSpeedMpS = Math.Min(speedInfo.MaxSpeedMpSSignal, Math.Min(allowedMaxSpeedLimitMpS, allowedMaxTempSpeedLimitMpS));
            }
            if (speedInfo.MaxSpeedMpSLimit > 0)
            {
                allowedMaxSpeedLimitMpS = Simulator.TimetableMode ? speedInfo.MaxSpeedMpSLimit : allowedAbsoluteMaxSpeedLimitMpS;
                if (Simulator.TimetableMode)
                    AllowedMaxSpeedMpS = speedInfo.MaxSpeedMpSLimit;
                else
                    AllowedMaxSpeedMpS = Math.Min(speedInfo.MaxSpeedMpSLimit, Math.Min(allowedMaxSpeedSignalMpS, allowedMaxTempSpeedLimitMpS));
            }
            if (speedInfo.MaxTempSpeedMpSLimit > 0 && !Simulator.TimetableMode)
            {
                allowedMaxTempSpeedLimitMpS = allowedAbsoluteMaxTempSpeedLimitMpS;
                AllowedMaxSpeedMpS = Math.Min(speedInfo.MaxTempSpeedMpSLimit, Math.Min(allowedMaxSpeedSignalMpS, allowedMaxSpeedLimitMpS));
            }
#if DEBUG_REPORTS
            File.AppendAllText(@"C:\temp\printproc.txt", "Validated speedlimit : " +
               "Limit : " + allowedMaxSpeedLimitMpS.ToString() + " ; " +
               "Signal : " + allowedMaxSpeedSignalMpS.ToString() + " ; " +
               "Overall : " + AllowedMaxSpeedMpS.ToString() + "\n");

#endif
            if (IsActualPlayerTrain && AllowedMaxSpeedMpS > prevMaxSpeedMpS)
            {
                Simulator.OnAllowedSpeedRaised(this);
            }
        }

        //================================================================================================//
        /// <summary>
        /// Clear all active items on occupied track
        /// <\summary>

        public void ClearActiveSectionItems()
        {
            ClearSectionItem dummyItem = new ClearSectionItem(0.0f, 0);
            List<DistanceTravelledItem> activeActions = requiredActions.GetActions(99999999f, dummyItem.GetType());
            foreach (DistanceTravelledItem thisAction in activeActions)
            {
                if (thisAction is ClearSectionItem)
                {
                    ClearSectionItem sectionInfo = thisAction as ClearSectionItem;
                    int thisSectionIndex = sectionInfo.TrackSectionIndex;
                    TrackCircuitSection thisSection = signalRef.TrackCircuitList[thisSectionIndex];

                    if (!OccupiedTrack.Contains(thisSection))
                    {
                        thisSection.ClearOccupied(this, true);
                    }
                }
            }
        }

        //================================================================================================//
        /// <summary>
        /// Forced stop due to problems with other train
        /// <\summary>

        public void ForcedStop(String reason, string otherTrainName, int otherTrainNumber)
        {
            Trace.TraceInformation("Train {0} ({1}) stopped for train {2} ({3}) : {4}",
                    Name, Number, otherTrainName, otherTrainNumber, reason);

            if (Simulator.PlayerLocomotive != null && Simulator.PlayerLocomotive.Train == this)
            {
                var report = Simulator.Catalog.GetStringFmt("Train stopped due to problems with other train: train {0} , reason: {1}", otherTrainNumber, reason);

                if (Simulator.Confirmer != null) // As Confirmer may not be created until after a restore.
                    Simulator.Confirmer.Message(ConfirmLevel.Warning, report);

#if DEBUG_REPORTS
                File.AppendAllText(@"C:\temp\printproc.txt", report + "\n");
#endif
                if (CheckTrain)
                {
                    File.AppendAllText(@"C:\temp\checktrain.txt", report + "\n");
                }

            }

            if (LeadLocomotive != null)
                ((MSTSLocomotive)LeadLocomotive).SetEmergency(true);
        }

        //================================================================================================//
        /// <summary>
        /// Remove train
        /// <\summary>

        public virtual void RemoveTrain()
        {
            RemoveFromTrack();
            ClearDeadlocks();
            Simulator.Trains.Remove(this);
        }

        //================================================================================================//
        //
        // Remove train from not-occupied sections only (for reset after uncoupling)
        //

        public void RemoveFromTrackNotOccupied(TCSubpathRoute newSections)
        {
            // clear occupied track

            List<int> clearedSectionIndices = new List<int>();
            TrackCircuitSection[] tempSectionArray = new TrackCircuitSection[OccupiedTrack.Count]; // copy sections as list is cleared by ClearOccupied method
            OccupiedTrack.CopyTo(tempSectionArray);

            for (int iIndex = 0; iIndex < tempSectionArray.Length; iIndex++)
            {
                TrackCircuitSection thisSection = tempSectionArray[iIndex];
                int newRouteIndex = newSections.GetRouteIndex(thisSection.Index, 0);
                if (newRouteIndex < 0)
                {
                    thisSection.ClearOccupied(this, true);
                    clearedSectionIndices.Add(thisSection.Index);
                }
            }

            // clear outstanding clear sections for sections no longer occupied

            foreach (DistanceTravelledItem thisAction in requiredActions)
            {
                if (thisAction is ClearSectionItem)
                {
                    ClearSectionItem thisItem = thisAction as ClearSectionItem;
                    if (clearedSectionIndices.Contains(thisItem.TrackSectionIndex))
                    {
                        TrackCircuitSection thisSection = signalRef.TrackCircuitList[thisItem.TrackSectionIndex];
                        thisSection.ClearOccupied(this, true);
                    }
                }
            }
        }

        //================================================================================================//
        //
        // Remove train (after coupling or when train disappeared in multiplayer)
        //

        public void RemoveFromTrack()
        {
            // check if no reserved sections remain

            int presentIndex = PresentPosition[1].RouteListIndex;

            if (presentIndex >= 0)
            {
                for (int iIndex = presentIndex; iIndex < ValidRoute[0].Count; iIndex++)
                {
                    TCRouteElement thisElement = ValidRoute[0][iIndex];
                    TrackCircuitSection thisSection = signalRef.TrackCircuitList[thisElement.TCSectionIndex];
                    thisSection.RemoveTrain(this, true);
                }
            }

            // for explorer (e.g. in Multiplayer) and manual mode check also backward route

            if (ValidRoute[1] != null && ValidRoute[1].Count > 0)
            {
                for(int iIndex = 0; iIndex < ValidRoute[1].Count; iIndex++)
                {
                    TCRouteElement thisElement = ValidRoute[1][iIndex];
                    TrackCircuitSection thisSection = signalRef.TrackCircuitList[thisElement.TCSectionIndex];
                    thisSection.RemoveTrain(this, true);
                }
            }

            // clear occupied track

            TrackCircuitSection[] tempSectionArray = new TrackCircuitSection[OccupiedTrack.Count]; // copy sections as list is cleared by ClearOccupied method
            OccupiedTrack.CopyTo(tempSectionArray);

            for (int iIndex = 0; iIndex < tempSectionArray.Length; iIndex++)
            {
                TrackCircuitSection thisSection = tempSectionArray[iIndex];
                thisSection.ClearOccupied(this, true);
            }

            // clear last reserved section
            LastReservedSection[0] = -1;
            LastReservedSection[1] = -1;

            // clear outstanding clear sections

            foreach (DistanceTravelledItem thisAction in requiredActions)
            {
                if (thisAction is ClearSectionItem)
                {
                    ClearSectionItem thisItem = thisAction as ClearSectionItem;
                    TrackCircuitSection thisSection = signalRef.TrackCircuitList[thisItem.TrackSectionIndex];
                    thisSection.ClearOccupied(this, true);
                }
            }
        }

        //================================================================================================//
        //
        // Update track actions after coupling
        //

        public void UpdateTrackActionsCoupling(bool couple_to_front)
        {

#if DEBUG_REPORTS
            File.AppendAllText(@"C:\temp\printproc.txt",
                            "Train " + Number.ToString() +
                            " coupled (front : " + couple_to_front.ToString() +
            " ) while on section " + PresentPosition[0].TCSectionIndex.ToString() + "\n");
#endif
            if (CheckTrain)
            {
                File.AppendAllText(@"C:\temp\checktrain.txt",
                                "Train " + Number.ToString() +
                                " coupled (front : " + couple_to_front.ToString() +
                " ) while on section " + PresentPosition[0].TCSectionIndex.ToString() + "\n");
            }

            // remove train from track - clear all reservations etc.

            RemoveFromTrack();
            ClearDeadlocks();

            // check if new train is freight or not

            CheckFreight();

            // clear all track occupation actions

            ClearSectionItem dummyItem = new ClearSectionItem(0.0f, 0);
            List<DistanceTravelledItem> activeActions = requiredActions.GetActions(99999999f, dummyItem.GetType());
            activeActions.Clear();

            // save existing TCPositions

            TCPosition oldPresentPosition = new TCPosition();
            PresentPosition[0].CopyTo(ref oldPresentPosition);
            TCPosition oldRearPosition = new TCPosition();
            PresentPosition[1].CopyTo(ref oldRearPosition);

            PresentPosition[0] = new TCPosition();
            PresentPosition[1] = new TCPosition();

            // create new TCPositions

            TrackNode tn = FrontTDBTraveller.TN;
            float offset = FrontTDBTraveller.TrackNodeOffset;
            int direction = (int)FrontTDBTraveller.Direction;

            PresentPosition[0].SetTCPosition(tn.TCCrossReference, offset, direction);
            PresentPosition[0].CopyTo(ref PreviousPosition[0]);

            tn = RearTDBTraveller.TN;
            offset = RearTDBTraveller.TrackNodeOffset;
            direction = (int)RearTDBTraveller.Direction;

            PresentPosition[1].SetTCPosition(tn.TCCrossReference, offset, direction);

            PresentPosition[0].DistanceTravelledM = DistanceTravelledM;
            PresentPosition[1].DistanceTravelledM = oldRearPosition.DistanceTravelledM;

            // use difference in position to update existing DistanceTravelled

            float deltaoffset = 0.0f;

            if (couple_to_front)
            {
                float offset_old = oldPresentPosition.TCOffset;
                float offset_new = PresentPosition[0].TCOffset;

                if (oldPresentPosition.TCSectionIndex == PresentPosition[0].TCSectionIndex)
                {
                    deltaoffset = offset_new - offset_old;
                }
                else
                {
                    TrackCircuitSection thisSection = signalRef.TrackCircuitList[oldPresentPosition.TCSectionIndex];
                    deltaoffset = thisSection.Length - offset_old;
                    deltaoffset += offset_new;

                    for (int iIndex = oldPresentPosition.RouteListIndex + 1; iIndex < PresentPosition[0].RouteListIndex; iIndex++)
                    {
                        thisSection = signalRef.TrackCircuitList[ValidRoute[0][iIndex].TCSectionIndex];
                        deltaoffset += thisSection.Length;
                    }
                }
                PresentPosition[0].DistanceTravelledM += deltaoffset;
                DistanceTravelledM += deltaoffset;
            }
            else
            {
                float offset_old = oldRearPosition.TCOffset;
                float offset_new = PresentPosition[1].TCOffset;

                if (oldRearPosition.TCSectionIndex == PresentPosition[1].TCSectionIndex)
                {
                    deltaoffset = offset_old - offset_new;
                }
                else
                {
                    deltaoffset = offset_old;
                    TrackCircuitSection thisSection = signalRef.TrackCircuitList[PresentPosition[1].TCSectionIndex];
                    deltaoffset += (thisSection.Length - offset_new);

                    for (int iIndex = oldRearPosition.RouteListIndex - 1; iIndex > PresentPosition[1].RouteListIndex; iIndex--)
                    {
                        thisSection = signalRef.TrackCircuitList[ValidRoute[0][iIndex].TCSectionIndex];
                        deltaoffset += thisSection.Length;
                    }
                }
                PresentPosition[1].DistanceTravelledM -= deltaoffset;
            }

            // Set track sections to occupied - forward direction only
            OccupiedTrack.Clear();
            UpdateOccupancies();

            // add sections to required actions list

            foreach (TrackCircuitSection thisSection in OccupiedTrack)
            {
                float distanceToClear = DistanceTravelledM + thisSection.Length + standardOverlapM;
                if (thisSection.CircuitType == TrackCircuitSection.TrackCircuitType.Junction ||
                    thisSection.CircuitType == TrackCircuitSection.TrackCircuitType.Crossover)
                {
                    distanceToClear += Length + junctionOverlapM;
                }

                if (PresentPosition[0].TCSectionIndex == thisSection.Index)
                {
                    distanceToClear += Length - PresentPosition[0].TCOffset;
                }
                else if (PresentPosition[1].TCSectionIndex == thisSection.Index)
                {
                    distanceToClear -= PresentPosition[1].TCOffset;
                }
                else
                {
                    distanceToClear += Length;
                }
                requiredActions.InsertAction(new ClearSectionItem(distanceToClear, thisSection.Index));
            }

            // rebuild list of station stops

            if (StationStops.Count > 0)
            {
                int presentStop = StationStops[0].PlatformReference;
                StationStops.Clear();
                HoldingSignals.Clear();

                BuildStationList(15.0f);

                bool removeStations = false;
                for (int iStation = StationStops.Count - 1; iStation >= 0; iStation--)
                {
                    if (removeStations)
                    {
                        if (StationStops[iStation].ExitSignal >= 0 && HoldingSignals.Contains(StationStops[iStation].ExitSignal))
                        {
                            HoldingSignals.Remove(StationStops[iStation].ExitSignal);
                        }
                        StationStops.RemoveAt(iStation);
                    }

                    if (StationStops[iStation].PlatformReference == presentStop)
                    {
                        removeStations = true;
                    }
                }
            }

            // add present occupied sections to train route to avoid out-of-path detection

            AddTrackSections();
 
            // reset signals etc.

            SignalObjectItems.Clear();
            NextSignalObject[0] = null;
            NextSignalObject[1] = null;
            LastReservedSection[0] = PresentPosition[0].TCSectionIndex;
            LastReservedSection[1] = PresentPosition[0].TCSectionIndex;

            InitializeSignals(true);

            if (TCRoute != null && (ControlMode == TRAIN_CONTROL.AUTO_SIGNAL || ControlMode == TRAIN_CONTROL.AUTO_NODE))
            {
                PresentPosition[0].RouteListIndex = ValidRoute[0].GetRouteIndex(PresentPosition[0].TCSectionIndex, 0);
                PresentPosition[1].RouteListIndex = ValidRoute[0].GetRouteIndex(PresentPosition[1].TCSectionIndex, 0);

                SwitchToNodeControl(PresentPosition[0].TCSectionIndex);
                CheckDeadlock(ValidRoute[0], Number);
                TCRoute.SetReversalOffset(Length, Simulator.TimetableMode);
            }
            else if (ControlMode == TRAIN_CONTROL.MANUAL)
            {
                // set track occupation

                UpdateSectionStateManual();

                // reset routes and check sections either end of train

                PresentPosition[0].RouteListIndex = -1;
                PresentPosition[1].RouteListIndex = -1;
                PreviousPosition[0].RouteListIndex = -1;

                UpdateManualMode(-1);
            }
            else if (ControlMode == TRAIN_CONTROL.EXPLORER)
            {
                // set track occupation

                UpdateSectionStateExplorer();

                // reset routes and check sections either end of train

                PresentPosition[0].RouteListIndex = -1;
                PresentPosition[1].RouteListIndex = -1;
                PreviousPosition[0].RouteListIndex = -1;

                UpdateExplorerMode(-1);
            }
            else
            {
                signalRef.requestClearNode(routedForward, ValidRoute[0]);
            }

#if DEBUG_REPORTS
            File.AppendAllText(@"C:\temp\printproc.txt",
                            "Train " + Number.ToString() +
                            " couple procedure completed \n");
#endif
            if (CheckTrain)
            {
                File.AppendAllText(@"C:\temp\checktrain.txt",
                                "Train " + Number.ToString() +
                                " couple procedure completed \n");
            }
        }

        //================================================================================================//
        //
        // Update occupancies
        // Update track occupancies after coupling
        //
        public void UpdateOccupancies()
        {
            if (TrainRoute != null) TrainRoute.Clear();
            TrainRoute = signalRef.BuildTempRoute(this, PresentPosition[1].TCSectionIndex, PresentPosition[1].TCOffset,
                PresentPosition[1].TCDirection, Length, false, true, false);

            foreach (TCRouteElement thisElement in TrainRoute)
            {
                TrackCircuitSection thisSection = signalRef.TrackCircuitList[thisElement.TCSectionIndex];
                thisSection.Reserve(routedForward, TrainRoute);
                if (!thisSection.CircuitState.ThisTrainOccupying(this))
                    thisSection.SetOccupied(routedForward);
            }
        }

        //================================================================================================//
        //
        // AddTrackSections
        // Add track sections not present in path to avoid out-of-path detection
        //

        public void AddTrackSections()
        {
            // check if first section in route

            if (ValidRoute[0].GetRouteIndex(OccupiedTrack[0].Index, 0) > 0)
            {
                int lastSectionIndex = OccupiedTrack[0].Index;
                int lastIndex = ValidRoute[0].GetRouteIndex(lastSectionIndex, 0);

                for (int isection = 1; isection <= OccupiedTrack.Count - 1; isection++)
                {
                    int nextSectionIndex = OccupiedTrack[isection].Index;
                    int nextIndex = ValidRoute[0].GetRouteIndex(nextSectionIndex, 0);

                    if (nextIndex < 0) // this section is not in route - if last index = 0, add to start else add to rear
                    {
                        TrackCircuitSection thisSection = signalRef.TrackCircuitList[nextSectionIndex];
                        int thisDirection = 0;

                        for (int iLink = 0; iLink <= 1; iLink++)
                        {
                            for (int iDir = 0; iDir <= 1; iDir++)
                            {
                                if (thisSection.Pins[iDir, iLink].Link == lastSectionIndex)
                                {
                                    thisDirection = thisSection.Pins[iDir, iLink].Direction;
                                    break;
                                }
                            }
                        }

                        if (lastIndex == 0)
                        {
                            ValidRoute[0].Insert(0, new TCRouteElement(OccupiedTrack[isection], thisDirection, signalRef, lastSectionIndex));
                        }
                        else
                        {
                            ValidRoute[0].Add(new TCRouteElement(OccupiedTrack[isection], thisDirection, signalRef, lastSectionIndex));
                        }
                    }
                    else
                    {
                        lastIndex = nextIndex;
                        lastSectionIndex = nextSectionIndex;
                    }
                }
            }
            // else start from last section
            else
            {
                int otIndex = OccupiedTrack.Count - 1;
                int lastSectionIndex = OccupiedTrack[otIndex].Index;
                int lastIndex = ValidRoute[0].GetRouteIndex(lastSectionIndex, 0);

                for (int isection = otIndex - 1; isection >= 0; isection--)
                {
                    int nextSectionIndex = OccupiedTrack[isection].Index;
                    int nextIndex = ValidRoute[0].GetRouteIndex(nextSectionIndex, 0);

                    if (nextIndex < 0) // this section is not in route - if last index = 0, add to start else add to rear
                    {
                        TrackCircuitSection thisSection = signalRef.TrackCircuitList[nextSectionIndex];
                        int thisDirection = 0;

                        for (int iLink = 0; iLink <= 1; iLink++)
                        {
                            for (int iDir = 0; iDir <= 1; iDir++)
                            {
                                if (thisSection.Pins[iDir, iLink].Link == lastSectionIndex)
                                {
                                    thisDirection = thisSection.Pins[iDir, iLink].Direction;
                                    break;
                                }
                            }
                        }

                        if (lastIndex == 0)
                        {
                            ValidRoute[0].Insert(0, new TCRouteElement(OccupiedTrack[isection], thisDirection, signalRef, lastSectionIndex));
                        }
                        else
                        {
                            ValidRoute[0].Add(new TCRouteElement(OccupiedTrack[isection], thisDirection, signalRef, lastSectionIndex));
                        }
                    }
                    else
                    {
                        lastIndex = nextIndex;
                        lastSectionIndex = nextSectionIndex;
                    }
                }
            }
        }

        //================================================================================================//
        //
        // Update track details after uncoupling
        //

        public bool UpdateTrackActionsUncoupling(bool originalTrain)
        {
            bool inPath = true;

#if DEBUG_REPORTS
            File.AppendAllText(@"C:\temp\printproc.txt",
                            "Train " + Number.ToString() +
                            " uncouple actions, org train : " + originalTrain.ToString() +
                " ; new type : " + TrainType.ToString() + "\n");
#endif
            if (CheckTrain)
            {
                File.AppendAllText(@"C:\temp\checktrain.txt",
                                "Train " + Number.ToString() +
                            " uncouple actions, org train : " + originalTrain.ToString() +
                " ; new type : " + TrainType.ToString() + "\n");
            }

            if (originalTrain)
            {
                RemoveFromTrack();
                ClearDeadlocks();

                ClearSectionItem dummyItem = new ClearSectionItem(0.0f, 0);
                List<DistanceTravelledItem> activeActions = requiredActions.GetActions(99999999f, dummyItem.GetType());
                activeActions.Clear();
            }

            // create new TCPositions

            TrackNode tn = FrontTDBTraveller.TN;
            float offset = FrontTDBTraveller.TrackNodeOffset;
            int direction = (int)FrontTDBTraveller.Direction;

            PresentPosition[0].SetTCPosition(tn.TCCrossReference, offset, direction);
            PresentPosition[0].CopyTo(ref PreviousPosition[0]);

            tn = RearTDBTraveller.TN;
            offset = RearTDBTraveller.TrackNodeOffset;
            direction = (int)RearTDBTraveller.Direction;

            PresentPosition[1].SetTCPosition(tn.TCCrossReference, offset, direction);

            PresentPosition[0].DistanceTravelledM = DistanceTravelledM;
            PresentPosition[1].DistanceTravelledM = DistanceTravelledM - Length;

            // Set track sections to occupied

            OccupiedTrack.Clear();

            // build route of sections now occupied
            OccupiedTrack.Clear();
            if (TrainRoute != null) TrainRoute.Clear();
            TrainRoute = signalRef.BuildTempRoute(this, PresentPosition[1].TCSectionIndex, PresentPosition[1].TCOffset,
                PresentPosition[1].TCDirection, Length, false, true, false);

            TrackCircuitSection thisSection;


            // static train

            if (TrainType == TRAINTYPE.STATIC)
            {

                // clear routes, required actions, traffic details

                ControlMode = TRAIN_CONTROL.UNDEFINED;
                if (TCRoute != null)
                {
                    if (TCRoute.TCRouteSubpaths != null) TCRoute.TCRouteSubpaths.Clear();
                    if (TCRoute.TCAlternativePaths != null) TCRoute.TCAlternativePaths.Clear();
                    TCRoute.activeAltpath = -1;
                }
                if (ValidRoute[0] != null && ValidRoute[0].Count > 0)
                {
                    signalRef.BreakDownRouteList(ValidRoute[0], 0, routedForward);
                    ValidRoute[0].Clear();
                }
                if (ValidRoute[1] != null && ValidRoute[1].Count > 0)
                {
                    signalRef.BreakDownRouteList(ValidRoute[1], 0, routedBackward);
                    ValidRoute[1].Clear();
                }
                requiredActions.Clear();

                if (TrafficService != null)
                    TrafficService.TrafficDetails.Clear();

                // build dummy route

                thisSection = signalRef.TrackCircuitList[PresentPosition[1].TCSectionIndex];
                offset = PresentPosition[1].TCOffset;

                ValidRoute[0] = signalRef.BuildTempRoute(this, thisSection.Index, PresentPosition[1].TCOffset,
                            PresentPosition[1].TCDirection, Length, true, true, false);

                foreach (TCRouteElement thisElement in TrainRoute)
                {
                    thisSection = signalRef.TrackCircuitList[thisElement.TCSectionIndex];
                    thisSection.SetOccupied(routedForward);
                }

            }

            // player train or AI train

            else
            {

                //<CSComment> InitializeSignals needs this info sometimes, so I repeat lines below here
                if (Simulator.Settings.ExtendedAIShunting && !IsActualPlayerTrain && (ControlMode == TRAIN_CONTROL.AUTO_SIGNAL || ControlMode == TRAIN_CONTROL.AUTO_NODE))
                {
                    while (TCRoute.activeSubpath <= TCRoute.TCRouteSubpaths.Count - 1)
                    {
                        PresentPosition[0].RouteListIndex = ValidRoute[0].GetRouteIndex(PresentPosition[0].TCSectionIndex, 0);
                        PresentPosition[1].RouteListIndex = ValidRoute[0].GetRouteIndex(PresentPosition[1].TCSectionIndex, 0);
                        if (PresentPosition[0].RouteListIndex < 0 || PresentPosition[1].RouteListIndex < 0)
                        {
                            // Try first to change valid route, if there are other subpaths.
                            if (TCRoute.activeSubpath < TCRoute.TCRouteSubpaths.Count - 1)
                            {
                                ValidRoute[0] = null;
                                TCRoute.activeSubpath++;
                                ValidRoute[0] = TCRoute.TCRouteSubpaths[TCRoute.activeSubpath];
                            }
                            else
                            {
                                inPath = false;
                                return inPath;
                            }
                        }
                        else
                        {
                            if (PresentPosition[0].TCDirection != ValidRoute[0][PresentPosition[0].RouteListIndex].Direction)
                            // Train must be reverted
                            {
                                ReverseFormation(false);
                                var tempTCPosition = PresentPosition[0];
                                PresentPosition[0] = PresentPosition[1];
                                PresentPosition[1] = tempTCPosition;
                            }
                            break;
                        }
                    }
                }

                foreach (TCRouteElement thisElement in TrainRoute)
                {
                    thisSection = signalRef.TrackCircuitList[thisElement.TCSectionIndex];
                    thisSection.SetOccupied(routedForward);
                }
                // rebuild list of station stops

                if (StationStops.Count > 0)
                {
                    int presentStop = StationStops[0].PlatformReference;
                    StationStops.Clear();
                    HoldingSignals.Clear();

                    BuildStationList(15.0f);

                    bool removeStations = false;
                    for (int iStation = StationStops.Count - 1; iStation >= 0; iStation--)
                    {
                        if (removeStations)
                        {
                            if (StationStops[iStation].ExitSignal >= 0 && StationStops[iStation].HoldSignal && HoldingSignals.Contains(StationStops[iStation].ExitSignal))
                            {
                                HoldingSignals.Remove(StationStops[iStation].ExitSignal);
                            }
                            StationStops.RemoveAt(iStation);
                        }

                        if (StationStops[iStation].PlatformReference == presentStop)
                        {
                            removeStations = true;
                        }
                    }
                }

                Reinitialize();
            }

#if DEBUG_REPORTS
            File.AppendAllText(@"C:\temp\printproc.txt",
                            "Train " + Number.ToString() +
                            " uncouple procedure completed \n");
#endif
            if (CheckTrain)
            {
                File.AppendAllText(@"C:\temp\checktrain.txt",
                                "Train " + Number.ToString() +
                                " uncouple procedure completed \n");
            }
            return inPath;
        }

        //================================================================================================//
        //
        // Perform various reinitializations
        //

        public void Reinitialize()
        {
            // reset signals etc.

            SignalObjectItems.Clear();
            NextSignalObject[0] = null;
            NextSignalObject[1] = null;
            LastReservedSection[0] = PresentPosition[0].TCSectionIndex;
            LastReservedSection[1] = PresentPosition[1].TCSectionIndex;


            InitializeSignals(true);

            if (ControlMode == TRAIN_CONTROL.AUTO_SIGNAL || ControlMode == TRAIN_CONTROL.AUTO_NODE)
            {
                PresentPosition[0].RouteListIndex = ValidRoute[0].GetRouteIndex(PresentPosition[0].TCSectionIndex, 0);
                PresentPosition[1].RouteListIndex = ValidRoute[0].GetRouteIndex(PresentPosition[1].TCSectionIndex, 0);

                CheckDeadlock(ValidRoute[0], Number);
                SwitchToNodeControl(PresentPosition[0].TCSectionIndex);
                TCRoute.SetReversalOffset(Length, Simulator.TimetableMode);
            }
            else if (ControlMode == TRAIN_CONTROL.MANUAL)
            {
                // set track occupation

                UpdateSectionStateManual();

                // reset routes and check sections either end of train

                PresentPosition[0].RouteListIndex = -1;
                PresentPosition[1].RouteListIndex = -1;
                PreviousPosition[0].RouteListIndex = -1;

                UpdateManualMode(-1);
            }
            else if (ControlMode == TRAIN_CONTROL.EXPLORER)
            {
                // set track occupation

                UpdateSectionStateExplorer();

                // reset routes and check sections either end of train

                PresentPosition[0].RouteListIndex = -1;
                PresentPosition[1].RouteListIndex = -1;
                PreviousPosition[0].RouteListIndex = -1;

                UpdateExplorerMode(-1);
            }
            else
            {
                CheckDeadlock(ValidRoute[0], Number);
                signalRef.requestClearNode(routedForward, ValidRoute[0]);
            }
        }

        //================================================================================================//
        //
        // Temporarily remove from track to allow decoupled train to set occupied sections
        //

        public void TemporarilyRemoveFromTrack()
        {
            RemoveFromTrack();
            ClearDeadlocks();
            ClearSectionItem dummyItem = new ClearSectionItem(0.0f, 0);
            List<DistanceTravelledItem> activeActions = requiredActions.GetActions(99999999f, dummyItem.GetType());
            activeActions.Clear();
        }

        //================================================================================================//
        //
        // Goes to next active subpath
        //
        public void IncrementSubpath(Train thisTrain)
        {
            if (thisTrain.TCRoute.activeSubpath < thisTrain.TCRoute.TCRouteSubpaths.Count - 1)
            {
                thisTrain.TCRoute.activeSubpath++;
                thisTrain.ValidRoute[0] = thisTrain.TCRoute.TCRouteSubpaths[thisTrain.TCRoute.activeSubpath];
            }
        }


        //================================================================================================//
        //
        // Check on deadlock
        //

        internal void CheckDeadlock(TCSubpathRoute thisRoute, int thisNumber)
        {
            if (signalRef.UseLocationPassingPaths)
            {
                CheckDeadlock_locationBased(thisRoute, thisNumber);  // new location based logic
            }
            else
            {
                CheckDeadlock_pathBased(thisRoute, thisNumber);      // old path based logic
            }
        }

        //================================================================================================//
        //
        // Check on deadlock - old style path based logic
        //

        internal void CheckDeadlock_pathBased(TCSubpathRoute thisRoute, int thisNumber)
        {
            // clear existing deadlock info

            ClearDeadlocks();

            // build new deadlock info

            foreach (Train otherTrain in Simulator.Trains)
            {
                if (otherTrain.Number != thisNumber && otherTrain.TrainType != TRAINTYPE.STATIC)
                {
                    TCSubpathRoute otherRoute = otherTrain.ValidRoute[0];
                    Dictionary<int, int> otherRouteDict = otherRoute.ConvertRoute();

                    for (int iElement = 0; iElement < thisRoute.Count; iElement++)
                    {
                        TCRouteElement thisElement = thisRoute[iElement];
                        int thisSectionIndex = thisElement.TCSectionIndex;
                        TrackCircuitSection thisSection = signalRef.TrackCircuitList[thisSectionIndex];
                        int thisSectionDirection = thisElement.Direction;

                        if (thisSection.CircuitType != TrackCircuitSection.TrackCircuitType.Crossover)
                        {
                            if (otherRouteDict.ContainsKey(thisSectionIndex))
                            {
                                int otherTrainDirection = otherRouteDict[thisSectionIndex];
                                //<CSComment> Right part of OR clause refers to initial placement with trains back-to-back and running away one from the other</CSComment>
                                if (otherTrainDirection == thisSectionDirection ||
                                    (PresentPosition[1].TCSectionIndex == otherTrain.PresentPosition[1].TCSectionIndex && thisSectionIndex == PresentPosition[1].TCSectionIndex &&
                                    PresentPosition[1].TCOffset + otherTrain.PresentPosition[1].TCOffset - 1 > thisSection.Length))
                                {
                                    iElement = EndCommonSection(iElement, thisRoute, otherRoute); 
                                }
                                else
                                {
                                    int[] endDeadlock = SetDeadlock_pathBased(iElement, thisRoute, otherRoute, otherTrain);
                                    // use end of alternative path if set - if so, compensate for iElement++
                                    iElement = endDeadlock[1] > 0 ? --endDeadlock[1] : endDeadlock[0];
                                }
                            }
                        }
                    }
                }
            }
#if DEBUG_DEADLOCK
            File.AppendAllText(@"C:\Temp\deadlock.txt", "\n================= Check Deadlock \nTrain : " + Number.ToString() + "\n");

            foreach (KeyValuePair<int, List<Dictionary<int, int>>> thisDeadlock in DeadlockInfo)
            {
                File.AppendAllText(@"C:\Temp\deadlock.txt", "Section : " + thisDeadlock.Key.ToString() + "\n");
                foreach (Dictionary<int, int> actDeadlocks in thisDeadlock.Value)
                {
                    foreach (KeyValuePair<int, int> actDeadlockInfo in actDeadlocks)
                    {
                        File.AppendAllText(@"C:\Temp\deadlock.txt", "  Other Train : " + actDeadlockInfo.Key.ToString() +
                            " - end Sector : " + actDeadlockInfo.Value.ToString() + "\n");
                    }
                }
                File.AppendAllText(@"C:\Temp\deadlock.txt", "\n");
            }
#endif
        }

        //================================================================================================//
        //
        // Obtain deadlock details - old style path based logic
        //

        private int[] SetDeadlock_pathBased(int thisIndex, TCSubpathRoute thisRoute, TCSubpathRoute otherRoute, Train otherTrain)
        {
            int[] returnValue = new int[2];
            returnValue[1] = -1;  // set to no alternative path used

            TCRouteElement firstElement = thisRoute[thisIndex];
            int firstSectionIndex = firstElement.TCSectionIndex;
            bool allreadyActive = false;

            int thisTrainSection = firstSectionIndex;
            int otherTrainSection = firstSectionIndex;

            int thisTrainIndex = thisIndex;
            int otherTrainIndex = otherRoute.GetRouteIndex(firstSectionIndex, 0);

            int thisFirstIndex = thisTrainIndex;
            int otherFirstIndex = otherTrainIndex;

            TCRouteElement thisTrainElement = thisRoute[thisTrainIndex];
            TCRouteElement otherTrainElement = otherRoute[otherTrainIndex];

            // loop while not at end of route for either train and sections are equal
            // loop is also exited when alternative path is found for either train
            for (int iLoop = 0; ((thisFirstIndex + iLoop) <= (thisRoute.Count - 1)) && ((otherFirstIndex - iLoop)) >= 0 && (thisTrainSection == otherTrainSection); iLoop++)
            {
                thisTrainIndex = thisFirstIndex + iLoop;
                otherTrainIndex = otherFirstIndex - iLoop;

                thisTrainElement = thisRoute[thisTrainIndex];
                otherTrainElement = otherRoute[otherTrainIndex];
                thisTrainSection = thisTrainElement.TCSectionIndex;
                otherTrainSection = otherTrainElement.TCSectionIndex;

                if (thisTrainElement.StartAlternativePath != null)
                {
                    int endAlternativeSection = thisTrainElement.StartAlternativePath[1];
                    returnValue[1] = thisRoute.GetRouteIndex(endAlternativeSection, thisIndex);
                    break;
                }

                if (otherTrainElement.EndAlternativePath != null)
                {
                    int endAlternativeSection = otherTrainElement.EndAlternativePath[1];
                    returnValue[1] = thisRoute.GetRouteIndex(endAlternativeSection, thisIndex);
                    break;
                }

                TrackCircuitSection thisSection = signalRef.TrackCircuitList[thisTrainSection];

                if (thisSection.IsSet(otherTrain, true))
                {
                    allreadyActive = true;
                }
            }

            // get sections on which loop ended
            thisTrainElement = thisRoute[thisTrainIndex];
            thisTrainSection = thisTrainElement.TCSectionIndex;

            otherTrainElement = otherRoute[otherTrainIndex];
            otherTrainSection = otherTrainElement.TCSectionIndex;

            // if last sections are still equal - end of route reached for one of the trains
            // otherwise, last common sections was previous sections for this train
            int lastSectionIndex = (thisTrainSection == otherTrainSection) ? thisTrainSection :
                thisRoute[thisTrainIndex - 1].TCSectionIndex;

            // if section is not a junction, check if either route not ended, if so continue up to next junction
            TrackCircuitSection lastSection = signalRef.TrackCircuitList[lastSectionIndex];
            if (lastSection.CircuitType != TrackCircuitSection.TrackCircuitType.Junction)
            {
                bool endSectionFound = false;
                if (thisTrainIndex < (thisRoute.Count - 1))
                {
                    for (int iIndex = thisTrainIndex + 1; iIndex < thisRoute.Count - 1 && !endSectionFound; iIndex++)
                    {
                        lastSection = signalRef.TrackCircuitList[thisRoute[iIndex].TCSectionIndex];
                        endSectionFound = lastSection.CircuitType == TrackCircuitSection.TrackCircuitType.Junction;
                    }
                }

                else if (otherTrainIndex > 0)
                {
                    for (int iIndex = otherTrainIndex - 1; iIndex >= 0 && !endSectionFound; iIndex--)
                    {
                        lastSection = signalRef.TrackCircuitList[otherRoute[iIndex].TCSectionIndex];
                        endSectionFound = lastSection.CircuitType == TrackCircuitSection.TrackCircuitType.Junction;
                        if (lastSection.IsSet(otherTrain, true))
                        {
                            allreadyActive = true;
                        }
                    }
                }
                lastSectionIndex = lastSection.Index;
            }

            // set deadlock info for both trains

            SetDeadlockInfo(firstSectionIndex, lastSectionIndex, otherTrain.Number);
            otherTrain.SetDeadlockInfo(lastSectionIndex, firstSectionIndex, Number);

            if (allreadyActive)
            {
                TrackCircuitSection thisSection = signalRef.TrackCircuitList[lastSectionIndex];
                thisSection.SetDeadlockTrap(otherTrain, otherTrain.DeadlockInfo[lastSectionIndex]);
            }

            returnValue[0] = thisRoute.GetRouteIndex(lastSectionIndex, thisIndex);
            if (returnValue[0] < 0)
                returnValue[0] = thisTrainIndex;
            return (returnValue);
        }

        //================================================================================================//
        //
        // Check on deadlock - new style location based logic
        //

        internal void CheckDeadlock_locationBased(TCSubpathRoute thisRoute, int thisNumber)
        {
            // clear existing deadlock info

            ClearDeadlocks();

            // build new deadlock info

            foreach (Train otherTrain in Simulator.Trains)
            {
                bool validTrain = true;

                // check if not AI_Static

                if (otherTrain.GetAIMovementState() == AITrain.AI_MOVEMENT_STATE.AI_STATIC)
                {
                    validTrain = false;
                }

                if (otherTrain.Number != thisNumber && otherTrain.TrainType != TRAINTYPE.STATIC && validTrain)
                {
                    TCSubpathRoute otherRoute = otherTrain.ValidRoute[0];
                    Dictionary<int, int> otherRouteDict = otherRoute.ConvertRoute();

                    for (int iElement = 0; iElement < thisRoute.Count; iElement++)
                    {
                        TCRouteElement thisElement = thisRoute[iElement];
                        int thisSectionIndex = thisElement.TCSectionIndex;
                        TrackCircuitSection thisSection = signalRef.TrackCircuitList[thisSectionIndex];
                        int thisSectionDirection = thisElement.Direction;

                        if (thisSection.CircuitType != TrackCircuitSection.TrackCircuitType.Crossover)
                        {
                            if (otherRouteDict.ContainsKey(thisSectionIndex))
                            {
                                int otherTrainDirection = otherRouteDict[thisSectionIndex];
                                //<CSComment> Right part of OR clause refers to initial placement with trains back-to-back and running away one from the other</CSComment>
                                if (otherTrainDirection == thisSectionDirection ||
                                    (PresentPosition[1].TCSectionIndex == otherTrain.PresentPosition[1].TCSectionIndex && thisSectionIndex == PresentPosition[1].TCSectionIndex &&
                                    PresentPosition[1].TCOffset + otherTrain.PresentPosition[1].TCOffset - 1 > thisSection.Length))
                                {
                                    iElement = EndCommonSection(iElement, thisRoute, otherRoute);
  
                                }
                                else
                                {
                                    if (CheckRealDeadlock_locationBased(thisRoute, otherRoute, ref iElement))
                                    {
                                        int[] endDeadlock = SetDeadlock_locationBased(iElement, thisRoute, otherRoute, otherTrain);
                                        // use end of alternative path if set
                                        iElement = endDeadlock[1] > 0 ? --endDeadlock[1] : endDeadlock[0];
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }

        //================================================================================================//
        //
        // Obtain deadlock details - new style location based logic
        //

        private int[] SetDeadlock_locationBased(int thisIndex, TCSubpathRoute thisRoute, TCSubpathRoute otherRoute, Train otherTrain)
        {
            int[] returnValue = new int[2];
            returnValue[1] = -1;  // set to no alternative path used

            TCRouteElement firstElement = thisRoute[thisIndex];
            int firstSectionIndex = firstElement.TCSectionIndex;
            bool allreadyActive = false;

            int thisTrainSectionIndex = firstSectionIndex;
            int otherTrainSectionIndex = firstSectionIndex;

            // double index variables required as last valid index must be known when exiting loop
            int thisTrainIndex = thisIndex;
            int thisTrainNextIndex = thisTrainIndex;
            int otherTrainIndex = otherRoute.GetRouteIndex(firstSectionIndex, 0);
            int otherTrainNextIndex = otherTrainIndex;

            int thisFirstIndex = thisTrainIndex;
            int otherFirstIndex = otherTrainIndex;

            TCRouteElement thisTrainElement = thisRoute[thisTrainIndex];
            TCRouteElement otherTrainElement = otherRoute[otherTrainIndex];

            bool validPassLocation = false;
            int endSectionRouteIndex = -1;

            bool endOfLoop = false;

            // loop while not at end of route for either train and sections are equal
            // loop is also exited when alternative path is found for either train
            while (!endOfLoop)
            {
                thisTrainIndex = thisTrainNextIndex;
                thisTrainElement = thisRoute[thisTrainIndex];
                otherTrainIndex = otherTrainNextIndex;
                thisTrainSectionIndex = thisTrainElement.TCSectionIndex;

                otherTrainElement = otherRoute[otherTrainIndex];
                otherTrainSectionIndex = otherTrainElement.TCSectionIndex;

                TrackCircuitSection thisSection = signalRef.TrackCircuitList[thisTrainSectionIndex];

                // if sections not equal : test length of next not-common section, if long enough then exit loop
                if (thisTrainSectionIndex != otherTrainSectionIndex)
                {
                    int nextThisRouteIndex = thisTrainIndex;
                    TrackCircuitSection passLoopSection = signalRef.TrackCircuitList[ValidRoute[0][nextThisRouteIndex].TCSectionIndex];
                    int nextOtherRouteIndex = otherRoute.GetRouteIndex(passLoopSection.Index, otherTrainIndex);

                    float passLength = passLoopSection.Length;
                    bool endOfPassLoop = false;

                    while (!endOfPassLoop)
                    {
                        // loop is longer as at least one of the trains so is valid
                        if (passLength > Length || passLength > otherTrain.Length)
                        {
                            endOfPassLoop = true;
                            endOfLoop = true;
                        }

                        // get next section
                        else if (nextThisRouteIndex < ValidRoute[0].Count - 2)
                        {
                            nextThisRouteIndex++;
                            passLoopSection = signalRef.TrackCircuitList[ValidRoute[0][nextThisRouteIndex].TCSectionIndex];
                            nextOtherRouteIndex = otherRoute.GetRouteIndexBackward(passLoopSection.Index, otherTrainIndex);

                            // new common section after too short loop - not a valid deadlock point
                            if (nextOtherRouteIndex >= 0)
                            {
                                endOfPassLoop = true;
                                thisTrainNextIndex = nextThisRouteIndex;
                                otherTrainNextIndex = nextOtherRouteIndex;
                            }
                            else
                            {
                                passLength += passLoopSection.Length;
                            }
                        }

                        // end of route
                        else
                        {
                            endOfPassLoop = true;
                            endOfLoop = true;
                        }
                    }
                }

                // if section is a deadlock boundary, check available paths for both trains

                else
                {

                    List<int> thisTrainAllocatedPaths = new List<int>();
                    List<int> otherTrainAllocatedPaths = new List<int>();

                    bool gotoNextSection = true;

                    if (thisSection.DeadlockReference >= 0 && thisTrainElement.FacingPoint) // test for facing points only
                    {
                        bool thisTrainFits = false;
                        bool otherTrainFits = false;

                        int endSectionIndex = -1;

                        validPassLocation = true;

                        // get allocated paths for this train
                        DeadlockInfo thisDeadlockInfo = signalRef.DeadlockInfoList[thisSection.DeadlockReference];

                        // get allocated paths for this train - if none yet set, create references
                        int thisTrainReferenceIndex = thisDeadlockInfo.GetTrainAndSubpathIndex(Number, TCRoute.activeSubpath);
                        if (!thisDeadlockInfo.TrainReferences.ContainsKey(thisTrainReferenceIndex))
                        {
                            thisDeadlockInfo.SetTrainDetails(Number, TCRoute.activeSubpath, Length, ValidRoute[0], thisTrainIndex);
                        }

                        // if valid path for this train
                        if (thisDeadlockInfo.TrainReferences.ContainsKey(thisTrainReferenceIndex))
                        {
                            thisTrainAllocatedPaths = thisDeadlockInfo.TrainReferences[thisDeadlockInfo.GetTrainAndSubpathIndex(Number, TCRoute.activeSubpath)];

                            // if paths available, get end section and check train against shortest path
                            if (thisTrainAllocatedPaths.Count > 0)
                            {
                                endSectionIndex = thisDeadlockInfo.AvailablePathList[thisTrainAllocatedPaths[0]].EndSectionIndex;
                                endSectionRouteIndex = thisRoute.GetRouteIndex(endSectionIndex, thisTrainIndex);
                                Dictionary<int, bool> thisTrainFitList = thisDeadlockInfo.TrainLengthFit[thisDeadlockInfo.GetTrainAndSubpathIndex(Number, TCRoute.activeSubpath)];
                                foreach (int iPath in thisTrainAllocatedPaths)
                                {
                                    if (thisTrainFitList[iPath])
                                    {
                                        thisTrainFits = true;
                                        break;
                                    }
                                }
                            }
                        }
                        else
                        {
                            validPassLocation = false;
                        }

                        // get allocated paths for other train - if none yet set, create references
                        int otherTrainReferenceIndex = thisDeadlockInfo.GetTrainAndSubpathIndex(otherTrain.Number, otherTrain.TCRoute.activeSubpath);
                        if (!thisDeadlockInfo.TrainReferences.ContainsKey(otherTrainReferenceIndex))
                        {
                            int otherTrainElementIndex = otherTrain.ValidRoute[0].GetRouteIndexBackward(endSectionIndex, otherFirstIndex);
                            if (otherTrainElementIndex < 0) // train joins deadlock area on different node
                            {
                                validPassLocation = false;
                                thisDeadlockInfo.RemoveTrainAndSubpathIndex(otherTrain.Number, otherTrain.TCRoute.activeSubpath); // remove index as train has no valid path
                            }
                            else
                            {
                                thisDeadlockInfo.SetTrainDetails(otherTrain.Number, otherTrain.TCRoute.activeSubpath, otherTrain.Length,
                                    otherTrain.ValidRoute[0], otherTrainElementIndex);
                            }
                        }

                        // if valid path for other train
                        if (validPassLocation && thisDeadlockInfo.TrainReferences.ContainsKey(otherTrainReferenceIndex))
                        {
                            otherTrainAllocatedPaths =
                                thisDeadlockInfo.TrainReferences[thisDeadlockInfo.GetTrainAndSubpathIndex(otherTrain.Number, otherTrain.TCRoute.activeSubpath)];

                            // if paths available, get end section (if not yet set) and check train against shortest path
                            if (otherTrainAllocatedPaths.Count > 0)
                            {
                                if (endSectionRouteIndex < 0)
                                {
                                    endSectionIndex = thisDeadlockInfo.AvailablePathList[otherTrainAllocatedPaths[0]].EndSectionIndex;
                                    endSectionRouteIndex = thisRoute.GetRouteIndex(endSectionIndex, thisTrainIndex);
                                }

                                Dictionary<int, bool> otherTrainFitList =
                                    thisDeadlockInfo.TrainLengthFit[thisDeadlockInfo.GetTrainAndSubpathIndex(otherTrain.Number, otherTrain.TCRoute.activeSubpath)];
                                foreach (int iPath in otherTrainAllocatedPaths)
                                {
                                    if (otherTrainFitList[iPath])
                                    {
                                        otherTrainFits = true;
                                        break;
                                    }
                                }
                            }
                        }
                        else
                        // other train has no valid path relating to the passing path, so passing not possible
                        {
                            validPassLocation = false;
                        }

                        // if both trains have only one route, make sure it's not the same (inverse) route

                        if (thisTrainAllocatedPaths.Count == 1 && otherTrainAllocatedPaths.Count == 1)
                        {
                            if (thisDeadlockInfo.InverseInfo.ContainsKey(thisTrainAllocatedPaths[0]) && thisDeadlockInfo.InverseInfo[thisTrainAllocatedPaths[0]] == otherTrainAllocatedPaths[0])
                            {
                                validPassLocation = false;
                            }
                        }

                        // if there are passing paths and at least one train fits in shortest path, it is a valid location so break loop
                        if (validPassLocation)
                        {
                            gotoNextSection = false;
                            if (thisTrainFits || otherTrainFits)
                            {
                                if (thisSection.IsSet(otherTrain, true))
                                {
                                    allreadyActive = true;
                                }
                                endOfLoop = true;
                            }
                            else
                            {
                                thisTrainNextIndex = endSectionRouteIndex;
                                otherTrainNextIndex = otherRoute.GetRouteIndexBackward(endSectionIndex, otherTrainIndex);
                                if (otherTrainNextIndex < 0) endOfLoop = true;
                            }
                        }
                    }

                    // if loop not yet ended - not a valid pass location, move to next section (if available)

                    if (gotoNextSection)
                    {
                        // if this section is occupied by other train, break loop - further checks are of no use
                        if (thisSection.IsSet(otherTrain, true))
                        {
                            allreadyActive = true;
                            endOfLoop = true;
                        }
                        else
                        {
                            thisTrainNextIndex++;
                            otherTrainNextIndex--;

                            if (thisTrainNextIndex > thisRoute.Count - 1 || otherTrainNextIndex < 0)
                            {
                                endOfLoop = true; // end of path reached for either train
                            }
                        }
                    }
                }
            }

            // if valid pass location : set return index

            if (validPassLocation && endSectionRouteIndex >= 0)
            {
                returnValue[1] = endSectionRouteIndex;
            }

            // get sections on which loop ended
            thisTrainElement = thisRoute[thisTrainIndex];
            thisTrainSectionIndex = thisTrainElement.TCSectionIndex;

            otherTrainElement = otherRoute[otherTrainIndex];
            otherTrainSectionIndex = otherTrainElement.TCSectionIndex;

            // if last sections are still equal - end of route reached for one of the trains
            // otherwise, last common sections was previous sections for this train
            int lastSectionIndex = (thisTrainSectionIndex == otherTrainSectionIndex) ? thisTrainSectionIndex :
                thisRoute[thisTrainIndex - 1].TCSectionIndex;
            TrackCircuitSection lastSection = signalRef.TrackCircuitList[lastSectionIndex];

            // TODO : if section is not a junction but deadlock is allready active, wind back to last junction
            // if section is not a junction, check if either route not ended, if so continue up to next junction
            if (lastSection.CircuitType != TrackCircuitSection.TrackCircuitType.Junction)
            {
                bool endSectionFound = false;
                if (thisTrainIndex < (thisRoute.Count - 1))
                {
                    for (int iIndex = thisTrainIndex; iIndex < thisRoute.Count - 1 && !endSectionFound; iIndex++)
                    {
                        lastSection = signalRef.TrackCircuitList[thisRoute[iIndex].TCSectionIndex];
                        endSectionFound = lastSection.CircuitType == TrackCircuitSection.TrackCircuitType.Junction;
                    }
                }

                else if (otherTrainIndex > 0)
                {
                    for (int iIndex = otherTrainIndex; iIndex >= 0 && !endSectionFound; iIndex--)
                    {
                        lastSection = signalRef.TrackCircuitList[otherRoute[iIndex].TCSectionIndex];
                        endSectionFound = false;

                        // junction found - end of loop
                        if (lastSection.CircuitType == TrackCircuitSection.TrackCircuitType.Junction)
                        {
                            endSectionFound = true;
                        }
                        // train has active wait condition at this location - end of loop
                        else if (otherTrain.CheckWaitCondition(lastSection.Index))
                        {
                            endSectionFound = true;
                        }

                        if (lastSection.IsSet(otherTrain, true))
                        {
                            allreadyActive = true;
                        }
                    }
                }
                lastSectionIndex = lastSection.Index;
            }

            // set deadlock info for both trains

            SetDeadlockInfo(firstSectionIndex, lastSectionIndex, otherTrain.Number);
            otherTrain.SetDeadlockInfo(lastSectionIndex, firstSectionIndex, Number);

            if (allreadyActive)
            {
                lastSection.SetDeadlockTrap(otherTrain, otherTrain.DeadlockInfo[lastSectionIndex]);
                returnValue[1] = thisRoute.Count;  // set beyond end of route - no further checks required
            }

            // if any section occupied by own train, reverse deadlock is active

            TrackCircuitSection firstSection = signalRef.TrackCircuitList[firstSectionIndex];

            int firstRouteIndex = ValidRoute[0].GetRouteIndex(firstSectionIndex, 0);
            int lastRouteIndex = ValidRoute[0].GetRouteIndex(lastSectionIndex, 0);

            for (int iRouteIndex = firstRouteIndex; iRouteIndex < lastRouteIndex; iRouteIndex++)
            {
                TrackCircuitSection partSection = signalRef.TrackCircuitList[ValidRoute[0][iRouteIndex].TCSectionIndex];
                if (partSection.IsSet(this, true))
                {
                    firstSection.SetDeadlockTrap(this, DeadlockInfo[firstSectionIndex]);
                }
            }

            returnValue[0] = thisRoute.GetRouteIndex(lastSectionIndex, thisIndex);
            if (returnValue[0] < 0)
                returnValue[0] = thisTrainIndex;
            return (returnValue);
        }

        //================================================================================================//
        //
        // Check if conflict is real deadlock situation
        // Conditions :
        //   if section is part of deadlock definition, it is a deadlock
        //   if section has intermediate signals, it is a deadlock
        //   if section has no intermediate signals but there are signals on both approaches to the deadlock, it is not a deadlock
        // Return value : boolean to indicate it is a deadlock or not
        // If not a deadlock, the REF int elementIndex is set to index of the last common section (will be increased in the loop)
        //

        internal bool CheckRealDeadlock_locationBased(TCSubpathRoute thisRoute, TCSubpathRoute otherRoute, ref int elementIndex)
        {
            bool isValidDeadlock = false;

            TrackCircuitSection thisSection = signalRef.TrackCircuitList[thisRoute[elementIndex].TCSectionIndex];

            // check if section is start or part of deadlock definition
            if (thisSection.DeadlockReference >= 0 || (thisSection.DeadlockBoundaries != null && thisSection.DeadlockBoundaries.Count > 0))
            {
                return (true);
            }

            // loop through common section - if signal is found, it is a deadlock 

            bool validLoop = true;
            int otherRouteIndex = otherRoute.GetRouteIndex(thisSection.Index, 0);

            for (int iIndex = 0; validLoop; iIndex++)
            {
                int thisElementIndex = elementIndex + iIndex;
                int otherElementIndex = otherRouteIndex - iIndex;

                if (thisElementIndex > thisRoute.Count - 1) validLoop = false;
                if (otherElementIndex < 0) validLoop = false;

                if (validLoop)
                {
                    TrackCircuitSection thisRouteSection = signalRef.TrackCircuitList[thisRoute[thisElementIndex].TCSectionIndex];
                    TrackCircuitSection otherRouteSection = signalRef.TrackCircuitList[otherRoute[otherElementIndex].TCSectionIndex];

                    if (thisRouteSection.Index != otherRouteSection.Index)
                    {
                        validLoop = false;
                    }
                    else if (thisRouteSection.EndSignals[0] != null || thisRouteSection.EndSignals[1] != null)
                    {
                        isValidDeadlock = true;
                        validLoop = false;
                    }
                }
            }

            // if no signals along section, check if section is protected by signals - if so, it is not a deadlock
            // check only as far as maximum signal check distance

            if (!isValidDeadlock)
            {
                // this route backward first
                float totalDistance = 0.0f;
                bool thisSignalFound = false;
                validLoop = true;

                for (int iIndex = 0; validLoop; iIndex--)
                {
                    int thisElementIndex = elementIndex + iIndex; // going backward as iIndex is negative!
                    if (thisElementIndex < 0)
                    {
                        validLoop = false;
                    }
                    else
                    {
                        TCRouteElement thisElement = thisRoute[thisElementIndex];
                        TrackCircuitSection thisRouteSection = signalRef.TrackCircuitList[thisElement.TCSectionIndex];
                        totalDistance += thisRouteSection.Length;

                        if (thisRouteSection.EndSignals[thisElement.Direction] != null)
                        {
                            validLoop = false;
                            thisSignalFound = true;
                        }

                        if (totalDistance > minCheckDistanceM) validLoop = false;
                    }
                }

                // other route backward next
                totalDistance = 0.0f;
                bool otherSignalFound = false;
                validLoop = true;

                for (int iIndex = 0; validLoop; iIndex--)
                {
                    int thisElementIndex = otherRouteIndex + iIndex; // going backward as iIndex is negative!
                    if (thisElementIndex < 0)
                    {
                        validLoop = false;
                    }
                    else
                    {
                        TCRouteElement thisElement = otherRoute[thisElementIndex];
                        TrackCircuitSection thisRouteSection = signalRef.TrackCircuitList[thisElement.TCSectionIndex];
                        totalDistance += thisRouteSection.Length;

                        if (thisRouteSection.EndSignals[thisElement.Direction] != null)
                        {
                            validLoop = false;
                            otherSignalFound = true;
                        }

                        if (totalDistance > minCheckDistanceM) validLoop = false;
                    }
                }

                if (!thisSignalFound || !otherSignalFound) isValidDeadlock = true;
            }

            // if not a valid deadlock, find end of common section

            if (!isValidDeadlock)
            {
                int newElementIndex = EndCommonSection(elementIndex, thisRoute, otherRoute);
                elementIndex = newElementIndex;
            }

            return (isValidDeadlock);
        }

        //================================================================================================//
        //
        // Set deadlock information
        //

        private void SetDeadlockInfo(int firstSection, int lastSection, int otherTrainNumber)
        {
            List<Dictionary<int, int>> DeadlockList = null;

            if (DeadlockInfo.ContainsKey(firstSection))
            {
                DeadlockList = DeadlockInfo[firstSection];
            }
            else
            {
                DeadlockList = new List<Dictionary<int, int>>();
                DeadlockInfo.Add(firstSection, DeadlockList);
            }
            Dictionary<int, int> thisDeadlock = new Dictionary<int, int>();
            thisDeadlock.Add(otherTrainNumber, lastSection);
            DeadlockList.Add(thisDeadlock);
        }

        //================================================================================================//
        //
        // Get end of common section
        //

        static int EndCommonSection(int thisIndex, TCSubpathRoute thisRoute, TCSubpathRoute otherRoute)
        {
            int firstSection = thisRoute[thisIndex].TCSectionIndex;

            int thisTrainSection = firstSection;
            int otherTrainSection = firstSection;

            int thisTrainIndex = thisIndex;
            int otherTrainIndex = otherRoute.GetRouteIndex(firstSection, 0);

            while (thisTrainSection == otherTrainSection && thisTrainIndex < (thisRoute.Count - 1) && otherTrainIndex > 0)
            {
                thisTrainIndex++;
                otherTrainIndex--;
                thisTrainSection = thisRoute[thisTrainIndex].TCSectionIndex;
                otherTrainSection = otherRoute[otherTrainIndex].TCSectionIndex;
            }

            return (thisTrainIndex);
        }

        //================================================================================================//
        //
        // Check if waiting for deadlock
        //

        public bool CheckDeadlockWait(SignalObject nextSignal)
        {

            bool deadlockWait = false;

            // check section list of signal for any deadlock traps

            foreach (TCRouteElement thisElement in nextSignal.signalRoute)
            {
                TrackCircuitSection thisSection = signalRef.TrackCircuitList[thisElement.TCSectionIndex];
                if (thisSection.DeadlockTraps.ContainsKey(Number))              // deadlock trap
                {
                    deadlockWait = true;

                    List<int> deadlockTrains = thisSection.DeadlockTraps[Number];

                    if (DeadlockInfo.ContainsKey(thisSection.Index) && !CheckWaitCondition(thisSection.Index)) // reverse deadlocks and not waiting
                    {
                        foreach (Dictionary<int, int> thisDeadlockList in DeadlockInfo[thisSection.Index])
                        {
                            foreach (KeyValuePair<int, int> thisDeadlock in thisDeadlockList)
                            {
                                if (!deadlockTrains.Contains(thisDeadlock.Key))
                                {
                                    TrackCircuitSection endSection = signalRef.TrackCircuitList[thisDeadlock.Value];
                                    endSection.SetDeadlockTrap(Number, thisDeadlock.Key);
                                }
                                else
                                {
                                    // check if train has reversal before end of path of other train
                                    if (TCRoute.TCRouteSubpaths.Count > (TCRoute.activeSubpath + 1))
                                    {
                                        Train otherTrain = GetOtherTrainByNumber(thisDeadlock.Key);

                                        bool commonSectionFound = false;
                                        bool lastReserved = false;
                                        for (int otherIndex = otherTrain.PresentPosition[0].RouteListIndex + 1;
                                             otherIndex < otherTrain.ValidRoute[0].Count - 1 && !commonSectionFound && !lastReserved;
                                             otherIndex++)
                                        {
                                            int sectionIndex = otherTrain.ValidRoute[0][otherIndex].TCSectionIndex;
                                            for (int ownIndex = PresentPosition[0].RouteListIndex; ownIndex < ValidRoute[0].Count - 1; ownIndex++)
                                            {
                                                if (sectionIndex == ValidRoute[0][ownIndex].TCSectionIndex)
                                                {
                                                    commonSectionFound = true;
                                                }
                                            }
                                            TrackCircuitSection otherSection = signalRef.TrackCircuitList[sectionIndex];
                                            if (otherSection.CircuitState.TrainReserved == null || otherSection.CircuitState.TrainReserved.Train.Number != otherTrain.Number)
                                            {
                                                lastReserved = true;
                                            }
                                            //if (sectionIndex == otherTrain.LastReservedSection[0]) lastReserved = true;
                                        }

                                        if (!commonSectionFound)
                                        {
                                            TrackCircuitSection endSection = signalRef.TrackCircuitList[thisDeadlock.Value];
                                            endSection.ClearDeadlockTrap(Number);
                                            thisSection.ClearDeadlockTrap(otherTrain.Number);
                                            deadlockWait = false;
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }

            return (deadlockWait);
        }

        //================================================================================================//
        /// <summary>
        /// Create station stop list
        /// <\summary>

        public void BuildStationList(float clearingDistanceM)
        {
            if (TrafficService == null)
                return;   // no traffic definition

            // loop through traffic points

            int beginActiveSubroute = 0;
            int activeSubrouteNodeIndex = 0;

            foreach (Traffic_Traffic_Item thisItem in TrafficService.TrafficDetails)
            {
                if (thisItem.ArrivalTime < 0)
                {
                    thisItem.ArrivalTime = thisItem.DepartTime < 0 ? TrafficService.Time : Math.Min(thisItem.DepartTime, TrafficService.Time);
                    Trace.TraceInformation("Train {0} Service {1} : Corrected negative arrival time within .trf or .act file", Number.ToString(), Name);
                }
                if (thisItem.DepartTime < 0)
                {
                    thisItem.DepartTime = Math.Max(thisItem.ArrivalTime, TrafficService.Time);
                    Trace.TraceInformation("Train {0} Service {1} : Corrected negative depart time within .trf or .act file", Number.ToString(), Name);
                }

                DateTime arriveDT = new DateTime((long)(Math.Pow(10, 7) * thisItem.ArrivalTime));
                DateTime departDT = new DateTime((long)(Math.Pow(10, 7) * thisItem.DepartTime));
                bool validStop =
                    CreateStationStop(thisItem.PlatformStartID, thisItem.ArrivalTime, thisItem.DepartTime, arriveDT, departDT, clearingDistanceM,
                    ref beginActiveSubroute, ref activeSubrouteNodeIndex);
                if (!validStop)
                {
                    Trace.TraceInformation("Train {0} Service {1} : cannot find platform {2}",
                        Number.ToString(), Name, thisItem.PlatformStartID.ToString());
                }
            }
        }

        //================================================================================================//
        /// <summary>
        /// Create station stop list
        /// <\summary>

        public bool CreateStationStop(int platformStartID, int arrivalTime, int departTime, DateTime arrivalDT, DateTime departureDT, float clearingDistanceM,
            ref int beginActiveSubroute, ref int activeSubrouteNodeIndex)
        {
            int platformIndex;
            int lastRouteIndex = 0;
            int activeSubroute = beginActiveSubroute;
            bool terminalStation = false;

            TCSubpathRoute thisRoute = TCRoute.TCRouteSubpaths[activeSubroute];

            // get platform details

            if (signalRef.PlatformXRefList.TryGetValue(platformStartID, out platformIndex))
            {
                PlatformDetails thisPlatform = signalRef.PlatformDetailsList[platformIndex];
                int sectionIndex = thisPlatform.TCSectionIndex[0];
                int routeIndex = thisRoute.GetRouteIndex(sectionIndex, activeSubrouteNodeIndex);
                // No backwards!
                if (routeIndex >=0 && StationStops.Count > 0 && StationStops[StationStops.Count - 1].RouteIndex == routeIndex
                    && StationStops[StationStops.Count - 1].SubrouteIndex == activeSubroute
                    && StationStops[StationStops.Count - 1].PlatformItem.TCOffset[1, thisRoute[routeIndex].Direction] >= thisPlatform.TCOffset[1, thisRoute[routeIndex].Direction])
                {
                    if (activeSubrouteNodeIndex < thisRoute.Count - 1) activeSubrouteNodeIndex++;
                    else if (activeSubroute < (TCRoute.TCRouteSubpaths.Count - 1))
                    {
                        activeSubroute++;
                        activeSubrouteNodeIndex = 0;
                        thisRoute = TCRoute.TCRouteSubpaths[activeSubroute];
                    }
                    else
                    {
                        Trace.TraceWarning("Train {0} Service {1} : platform {2} not in correct sequence",
                            Number.ToString(), Name, platformStartID.ToString());
                        return false;
                    }
                    routeIndex = thisRoute.GetRouteIndex(sectionIndex, activeSubrouteNodeIndex);
                }

                if (!Simulator.TimetableMode && routeIndex == thisRoute.Count -1 && TCRoute.ReversalInfo[activeSubroute].Valid)
                {
                    // Check if station beyond reversal point
                    var direction = thisRoute[routeIndex].Direction;
                    if (TCRoute.ReversalInfo[activeSubroute].ReverseReversalOffset < thisPlatform.TCOffset[0, direction])
                        routeIndex = -1;
                }


                // if first section not found in route, try last

                if (routeIndex < 0)
                {
                    sectionIndex = thisPlatform.TCSectionIndex[thisPlatform.TCSectionIndex.Count - 1];
                    routeIndex = thisRoute.GetRouteIndex(sectionIndex, activeSubrouteNodeIndex);
                    if (!Simulator.TimetableMode && routeIndex == thisRoute.Count - 1 && TCRoute.ReversalInfo[activeSubroute].Valid)
                    {
                        // Check if station beyond reversal point
                        var direction = thisRoute[routeIndex].Direction;
                        if (TCRoute.ReversalInfo[activeSubroute].ReverseReversalOffset < thisPlatform.TCOffset[0, direction])
                        {
                            routeIndex = -1;
                            // jump next subpath, because station stop can't be there
                            activeSubroute++;
                            activeSubrouteNodeIndex = 0;
                        }
                    }
                }

                // if neither section found - try next subroute - keep trying till found or out of subroutes

                while (routeIndex < 0 && activeSubroute < (TCRoute.TCRouteSubpaths.Count - 1))
                {
                    activeSubroute++;
                    activeSubrouteNodeIndex = 0;
                    thisRoute = TCRoute.TCRouteSubpaths[activeSubroute];
                    routeIndex = thisRoute.GetRouteIndex(sectionIndex, activeSubrouteNodeIndex);
                    if (!Simulator.TimetableMode && routeIndex == thisRoute.Count - 1 && TCRoute.ReversalInfo[activeSubroute].Valid)
                    {
                        // Check if station beyond reversal point
                        var direction = thisRoute[routeIndex].Direction;
                        if (TCRoute.ReversalInfo[activeSubroute].ReverseReversalOffset < thisPlatform.TCOffset[0, direction])
                            routeIndex = -1;
                    }
                    // if first section not found in route, try last

                    if (routeIndex < 0)
                    {
                        sectionIndex = thisPlatform.TCSectionIndex[thisPlatform.TCSectionIndex.Count - 1];
                        routeIndex = thisRoute.GetRouteIndex(sectionIndex, activeSubrouteNodeIndex);
                        if (!Simulator.TimetableMode && routeIndex == thisRoute.Count - 1 && TCRoute.ReversalInfo[activeSubroute].Valid)
                        {
                            // Check if station beyond reversal point
                            var direction = thisRoute[routeIndex].Direction;
                            if (TCRoute.ReversalInfo[activeSubroute].ReverseReversalOffset < thisPlatform.TCOffset[0, direction])
                            {
                                routeIndex = -1;
                                // jump next subpath, because station stop can't be there
                                activeSubroute++;
                                activeSubrouteNodeIndex = 0;
                            }
                        }
                    }
                }

                // if neither section found - platform is not on route - skip

                if (routeIndex < 0)
                {
                    Trace.TraceWarning("Train {0} Service {1} : platform {2} is not on route",
                            Number.ToString(), Name, platformStartID.ToString());
                    return (false);
                }
                else
                {
                    activeSubrouteNodeIndex = routeIndex;
                    beginActiveSubroute = activeSubroute;
                }

                // determine end stop position depending on direction

                TCRouteElement thisElement = thisRoute[routeIndex];

                int endSectionIndex = thisElement.Direction == 0 ?
                    thisPlatform.TCSectionIndex[thisPlatform.TCSectionIndex.Count - 1] :
                    thisPlatform.TCSectionIndex[0];
                int beginSectionIndex = thisElement.Direction == 0 ?
                    thisPlatform.TCSectionIndex[0] :
                    thisPlatform.TCSectionIndex[thisPlatform.TCSectionIndex.Count - 1];

                float endOffset = thisPlatform.TCOffset[1, thisElement.Direction];
                float beginOffset = thisPlatform.TCOffset[0, thisElement.Direction];

                float deltaLength = thisPlatform.Length - Length; // platform length - train length

                TrackCircuitSection endSection = signalRef.TrackCircuitList[endSectionIndex];


                int firstRouteIndex = thisRoute.GetRouteIndex(beginSectionIndex, 0);
                if (firstRouteIndex < 0)
                    firstRouteIndex = routeIndex;
                lastRouteIndex = thisRoute.GetRouteIndex(endSectionIndex, 0);
                if (lastRouteIndex < 0)
                    lastRouteIndex = routeIndex;

                // if train too long : search back for platform with same name

                float fullLength = thisPlatform.Length;

                if (deltaLength < 0)
                {
                    float actualBegin = beginOffset;

                    TrackCircuitSection thisSection = signalRef.TrackCircuitList[beginSectionIndex];

                    // Other platforms in same section

                    if (thisSection.PlatformIndex.Count > 1)
                    {
                        foreach (int nextIndex in thisSection.PlatformIndex)
                        {
                            if (nextIndex != platformIndex)
                            {
                                PlatformDetails otherPlatform = signalRef.PlatformDetailsList[nextIndex];
                                if (String.Compare(otherPlatform.Name, thisPlatform.Name) == 0)
                                {
                                    int otherSectionIndex = thisElement.Direction == 0 ?
                                        otherPlatform.TCSectionIndex[0] :
                                        otherPlatform.TCSectionIndex[thisPlatform.TCSectionIndex.Count - 1];
                                    if (otherSectionIndex == beginSectionIndex)
                                    {
                                        if (otherPlatform.TCOffset[0, thisElement.Direction] < actualBegin)
                                        {
                                            actualBegin = otherPlatform.TCOffset[0, thisElement.Direction];
                                            fullLength = endOffset - actualBegin;
                                        }
                                    }
                                    else
                                    {
                                        int addRouteIndex = thisRoute.GetRouteIndex(otherSectionIndex, 0);
                                        float addOffset = otherPlatform.TCOffset[1, thisElement.Direction == 0 ? 1 : 0];
                                        // offset of begin in other direction is length of available track

                                        if (lastRouteIndex > 0)
                                        {
                                            float thisLength =
                                                thisRoute.GetDistanceAlongRoute(addRouteIndex, addOffset,
                                                        lastRouteIndex, endOffset, true, signalRef);
                                            if (thisLength > fullLength)
                                                fullLength = thisLength;
                                        }
                                    }
                                }
                            }
                        }
                    }

                    deltaLength = fullLength - Length;
                }

                // search back along route

                if (deltaLength < 0)
                {
                    float distance = fullLength + beginOffset;
                    bool platformFound = false;

                    for (int iIndex = firstRouteIndex - 1;
                                iIndex >= 0 && distance < 500f && platformFound;
                                iIndex--)
                    {
                        int nextSectionIndex = thisRoute[iIndex].TCSectionIndex;
                        TrackCircuitSection nextSection = signalRef.TrackCircuitList[nextSectionIndex];

                        foreach (int otherPlatformIndex in nextSection.PlatformIndex)
                        {
                            PlatformDetails otherPlatform = signalRef.PlatformDetailsList[otherPlatformIndex];
                            if (String.Compare(otherPlatform.Name, thisPlatform.Name) == 0)
                            {
                                fullLength = otherPlatform.Length + distance;
                                // we miss a little bit (offset) - that's because we don't know direction of other platform
                                platformFound = true; // only check for one more
                            }
                        }
                        distance += nextSection.Length;
                    }

                    deltaLength = fullLength - Length;
                }

                // check whether terminal station or not
                TCSubpathRoute routeToEndOfTrack = signalRef.BuildTempRoute(this, endSectionIndex, endOffset, thisElement.Direction, 30, true, true, false);
                if (routeToEndOfTrack.Count > 0)
                {
                    TrackCircuitSection thisSection = signalRef.TrackCircuitList[routeToEndOfTrack[routeToEndOfTrack.Count - 1].TCSectionIndex];
                    if (thisSection.CircuitType == TrackCircuitSection.TrackCircuitType.EndOfTrack)
                    {
                        terminalStation = true;
                        foreach (TCRouteElement tcElement in routeToEndOfTrack)
                        {
                            thisSection = signalRef.TrackCircuitList[tcElement.TCSectionIndex];
                            if (thisSection.CircuitType == TrackCircuitSection.TrackCircuitType.Junction)
                            {
                                terminalStation = false;
                                break;
                            }
                        }
                    }
                }


                // determine stop position
                float stopOffset = endOffset - (0.5f * deltaLength);
                if (terminalStation && deltaLength > 0 && !Simulator.TimetableMode)
                        stopOffset = endOffset - 1;

                // beyond section : check for route validity (may not exceed route)

                if (stopOffset > endSection.Length)
                {
                    float addOffset = stopOffset - endSection.Length;
                    float overlap = 0f;

                    for (int iIndex = lastRouteIndex; iIndex < thisRoute.Count && overlap < addOffset; iIndex++)
                    {
                        TrackCircuitSection nextSection = signalRef.TrackCircuitList[thisRoute[iIndex].TCSectionIndex];
                        overlap += nextSection.Length;
                    }

                    if (overlap < stopOffset)
                        stopOffset = overlap;
                }

                // check if stop offset beyond end signal - do not hold at signal

                int EndSignal = -1;
                bool HoldSignal = false;
                bool NoWaitSignal = false;
                bool NoClaimAllowed = false;

                // check if train is to reverse in platform
                // if so, set signal at other end as hold signal

                int useDirection = thisElement.Direction;
                bool inDirection = true;

                if (TCRoute.ReversalInfo[activeSubroute].Valid)
                {
                    TCReversalInfo thisReversal = TCRoute.ReversalInfo[activeSubroute];
                    int reversalIndex = thisReversal.SignalUsed ? thisReversal.LastSignalIndex : thisReversal.LastDivergeIndex;
                    if (reversalIndex >= 0 && reversalIndex <= lastRouteIndex &&
                        (CheckVicinityOfPlatformToReversalPoint(thisPlatform.TCOffset[1, thisElement.Direction], activeSubrouteNodeIndex, activeSubroute) || Simulator.TimetableMode)
                        && !(reversalIndex == lastRouteIndex && thisReversal.ReverseReversalOffset - 50.0 > thisPlatform.TCOffset[1, thisElement.Direction])) // reversal point is this section or earlier
                    {
                        useDirection = useDirection == 0 ? 1 : 0;
                        inDirection = false;
                    }
                }

                // check for end signal

                if (thisPlatform.EndSignals[useDirection] >= 0)
                {
                    EndSignal = thisPlatform.EndSignals[useDirection];

                    // stop location is in front of signal
                    if (inDirection)
                    {
                        if (thisPlatform.DistanceToSignals[useDirection] > (stopOffset - endOffset))
                        {
                            HoldSignal = true;

                            if ((thisPlatform.DistanceToSignals[useDirection] + (endOffset - stopOffset)) < clearingDistanceM)
                            {
                                stopOffset = endOffset + thisPlatform.DistanceToSignals[useDirection] - clearingDistanceM - 1.0f;
                            }
                        }
                        // at terminal station we will stop just in front of signal
                        else if (terminalStation && deltaLength <= 0 && !Simulator.TimetableMode)
                        {
                            HoldSignal = true;
                            stopOffset = endOffset + thisPlatform.DistanceToSignals[useDirection] - 3.0f;
                        }
                        // if most of train fits in platform then stop at signal
                        else if ((thisPlatform.DistanceToSignals[useDirection] - clearingDistanceM + thisPlatform.Length) >
                                      (0.6 * Length))
                        {
                            HoldSignal = true;
                            stopOffset = endOffset + thisPlatform.DistanceToSignals[useDirection] - clearingDistanceM - 1.0f;
                            // set 1m earlier to give priority to station stop over signal
                        }
                        // train does not fit in platform - reset exit signal
                        else
                        {
                            EndSignal = -1;
                        }
                    }
                    else
                    // end of train is beyond signal
                    {
                        int oldUseDirection = useDirection == 1 ? 0 : 1;
                        if (thisPlatform.EndSignals[oldUseDirection] >= 0 && terminalStation && deltaLength <= 0 && !Simulator.TimetableMode)
                        {
                            // check also the back of train after reverse
                            stopOffset = endOffset + thisPlatform.DistanceToSignals[oldUseDirection] - 3.0f;
                        }
                        if ((beginOffset - thisPlatform.DistanceToSignals[useDirection]) < (stopOffset - Length))
                        {
                            HoldSignal = true;

                            if ((stopOffset - Length - beginOffset + thisPlatform.DistanceToSignals[useDirection]) < clearingDistanceM)
                            {
                                if (!(terminalStation && deltaLength > 0 && !Simulator.TimetableMode)) 
                                    stopOffset = beginOffset - thisPlatform.DistanceToSignals[useDirection] + Length + clearingDistanceM + 1.0f;
                            }
                        }
                        // if most of train fits in platform then stop at signal
                        else if ((thisPlatform.DistanceToSignals[useDirection] - clearingDistanceM + thisPlatform.Length) >
                                      (0.6 * Length))
                        {
                            // set 1m earlier to give priority to station stop over signal
                            if (!(terminalStation && deltaLength > 0 && !Simulator.TimetableMode))
                                stopOffset = beginOffset - thisPlatform.DistanceToSignals[useDirection] + Length + clearingDistanceM + 1.0f;

                            // check if stop is clear of end signal (if any)
                            if (thisPlatform.EndSignals[thisElement.Direction] != -1)
                            {
                                if (stopOffset < (endOffset + thisPlatform.DistanceToSignals[thisElement.Direction]))
                                {
                                    HoldSignal = true; // if train fits between signals
                                }
                                else
                                {
                                    if (!(terminalStation && deltaLength > 0 && !Simulator.TimetableMode))
                                        stopOffset = endOffset + thisPlatform.DistanceToSignals[thisElement.Direction] - 1.0f; // stop at end signal
                                }
                            }
                        }
                        // train does not fit in platform - reset exit signal
                        else
                        {
                            EndSignal = -1;
                        }
                    }
                }

                if (Simulator.Settings.NoForcedRedAtStationStops)
                {
                    // We don't want reds at exit signal in this case
                    HoldSignal = false;
                }

                // build and add station stop

                TCRouteElement lastElement = thisRoute[lastRouteIndex];

                StationStop thisStation = new StationStop(
                        platformStartID,
                        thisPlatform,
                        activeSubroute,
                        lastRouteIndex,
                        lastElement.TCSectionIndex,
                        thisElement.Direction,
                        EndSignal,
                        HoldSignal,
                        NoWaitSignal,
                        NoClaimAllowed,
                        stopOffset,
                        arrivalTime,
                        departTime,
                        false,
                        null,
                        null,
                        null,
                        false,
                        false,
                        false,
                        false,
                        false,
                        StationStop.STOPTYPE.STATION_STOP);

                thisStation.arrivalDT = arrivalDT;
                thisStation.departureDT = departureDT;

                StationStops.Add(thisStation);

                //<CSComment> should this be reused?

                // 
                //
                //                    // if station has hold signal and this signal is the same as the exit signal for previous station, remove the exit signal from the previous station
                //
                //                    if (HoldSignal && StationStops.Count > 1)
                //                    {
                //                        if (EndSignal == StationStops[StationStops.Count - 2].ExitSignal && StationStops[StationStops.Count - 2].HoldSignal)
                //                        {
                //                            StationStops[StationStops.Count - 2].HoldSignal = false;
                //                            StationStops[StationStops.Count - 2].ExitSignal = -1;
                //                            if (HoldingSignals.Contains(EndSignal))
                //                            {
                //                                HoldingSignals.Remove(EndSignal);
                //                            }
                //                        }
                //                    }


                // add signal to list of hold signals

                if (HoldSignal)
                {
                    HoldingSignals.Add(EndSignal);
                }
            }
            else
            {
                return (false);
            }

#if DEBUG_TEST
            File.AppendAllText(@"C:\temp\TCSections.txt", "\nSTATION STOPS\n\n");

            if (StationStops.Count <= 0)
            {
                File.AppendAllText(@"C:\temp\TCSections.txt", " No stops\n");
            }
            else
            {
                foreach (StationStop thisStation in StationStops)
                {
                    File.AppendAllText(@"C:\temp\TCSections.txt", "\n");
                    if (thisStation.PlatformItem == null)
                    {
                        File.AppendAllText(@"C:\temp\TCSections.txt", "Waiting Point");
                    }
                    else
                    {
                        File.AppendAllText(@"C:\temp\TCSections.txt", "Station : " + thisStation.PlatformItem.Name + "\n");
                        DateTime baseDT = new DateTime();
                        DateTime arrTime = baseDT.AddSeconds(thisStation.ArrivalTime);
                        File.AppendAllText(@"C:\temp\TCSections.txt", "Arrive  : " + arrTime.ToString("HH:mm:ss") + "\n");
                        DateTime depTime = baseDT.AddSeconds(thisStation.DepartTime);
                        File.AppendAllText(@"C:\temp\TCSections.txt", "Depart  : " + depTime.ToString("HH:mm:ss") + "\n");
                    }
                    File.AppendAllText(@"C:\temp\TCSections.txt", "Exit Sig: " + thisStation.ExitSignal.ToString() + "\n");
                    File.AppendAllText(@"C:\temp\TCSections.txt", "Hold Sig: " + thisStation.HoldSignal.ToString() + "\n");
                    File.AppendAllText(@"C:\temp\TCSections.txt", "Subpath : " + thisStation.SubrouteIndex.ToString() + "\n");
                    File.AppendAllText(@"C:\temp\TCSections.txt", "Index   : " + lastRouteIndex.ToString() + "\n");
                    File.AppendAllText(@"C:\temp\TCSections.txt", "Section : " + thisStation.TCSectionIndex.ToString() + "\n");
                    File.AppendAllText(@"C:\temp\TCSections.txt", "Direct  : " + thisStation.Direction.ToString() + "\n");
                    File.AppendAllText(@"C:\temp\TCSections.txt", "Stop    : " + thisStation.StopOffset.ToString("###0.00") + "\n");
                }
            }
#endif
            return (true);
        }

        //================================================================================================//
        /// <summary>
        /// Check whether train is at Platform
        /// returns true if yes
        /// </summary>

        public bool IsAtPlatform()
        {
            // build list of occupied section
            bool atStation = false;
            int frontIndex = PresentPosition[0].RouteListIndex;
            int rearIndex = PresentPosition[1].RouteListIndex;
            List<int> occupiedSections = new List<int>();

            // check valid positions
            if (frontIndex < 0 && rearIndex < 0) // not on route so cannot be in station
            {
                return atStation; // no further actions possible
            }

            // correct position if either end is off route
            if (frontIndex < 0) frontIndex = rearIndex;
            if (rearIndex < 0) rearIndex = frontIndex;

            // set start and stop in correct order
            int startIndex = frontIndex < rearIndex ? frontIndex : rearIndex;
            int stopIndex = frontIndex < rearIndex ? rearIndex : frontIndex;

            for (int iIndex = startIndex; iIndex <= stopIndex; iIndex++)
            {
                occupiedSections.Add(ValidRoute[0][iIndex].TCSectionIndex);
            }

            // check if any platform section is in list of occupied sections - if so, we're in the station
            foreach (int sectionIndex in StationStops[0].PlatformItem.TCSectionIndex)
            {
                if (occupiedSections.Contains(sectionIndex))
                {
                    // TODO : check offset within section
                    atStation = true;
                    break;
                }
            }
            return atStation;
        }

        //================================================================================================//
        /// <summary>
        /// Check whether train has missed platform
        /// returns true if yes
        /// </summary>

        public bool IsMissedPlatform(float thresholdDistance)
        {
            // check if station missed

            int stationRouteIndex = ValidRoute[0].GetRouteIndex(StationStops[0].TCSectionIndex, 0);

            if (StationStops[0].SubrouteIndex == TCRoute.activeSubpath)
            {
                if (stationRouteIndex < 0)
                {
                    return true;
                }
                else if (stationRouteIndex <= PresentPosition[1].RouteListIndex)
                {
                    var platformSection = signalRef.TrackCircuitList[StationStops[0].TCSectionIndex];
                    var platformReverseStopOffset = platformSection.Length - StationStops[0].StopOffset;
                    return ValidRoute[0].GetDistanceAlongRoute(stationRouteIndex, platformReverseStopOffset, PresentPosition[1].RouteListIndex, PresentPosition[1].TCOffset, true, signalRef) > thresholdDistance;
                }
            }
            return false;
        }

            //================================================================================================//
            /// <summary>
            /// Check vicinity of reversal point to Platform
            /// returns false if distance greater than preset value 
            /// </summary>

            public bool CheckVicinityOfPlatformToReversalPoint(float tcOffset, int routeListIndex, int activeSubpath)
        {
            float Threshold = 100.0f;
            float lengthToGoM = -tcOffset;
            TrackCircuitSection thisSection;
            if (routeListIndex == -1)
            {
                Trace.TraceWarning("Train {0} service {1}, platform off path; reversal point considered remote", Number, Name);
                return false;
            }
            int reversalRouteIndex =  TCRoute.TCRouteSubpaths[activeSubpath].GetRouteIndex(TCRoute.ReversalInfo[TCRoute.activeSubpath].ReversalSectionIndex, routeListIndex);
            if (reversalRouteIndex == -1)
            {
                Trace.TraceWarning("Train {0} service {1}, reversal or end point off path; reversal point considered remote", Number, Name);
                return false;
            }
            if (routeListIndex <= reversalRouteIndex)
            {
                for (int iElement = routeListIndex; iElement < TCRoute.TCRouteSubpaths[activeSubpath].Count; iElement++)
                {
                    TCRouteElement thisElement = TCRoute.TCRouteSubpaths[activeSubpath][iElement];
                    thisSection = signalRef.TrackCircuitList[thisElement.TCSectionIndex];
                    if (thisSection.Index == TCRoute.ReversalInfo[TCRoute.activeSubpath].ReversalSectionIndex)
                    {
                        break;
                    }
                    else
                    {
                        lengthToGoM += thisSection.Length;
                        if (lengthToGoM > Threshold) return false;
                    }
                }
                return lengthToGoM + TCRoute.ReversalInfo[TCRoute.activeSubpath].ReverseReversalOffset < Threshold;
            }
            else
                // platform is beyond reversal point
                return true;
        }

        //================================================================================================//
        /// <summary>
        /// Create waiting point list
        /// <\summary>

        public virtual void BuildWaitingPointList(float clearingDistanceM)
        {

            // loop through all waiting points - back to front as the processing affects the actual routepaths

            for (int iWait = 0; iWait <= TCRoute.WaitingPoints.Count - 1; iWait++)
            {
                int[] waitingPoint = TCRoute.WaitingPoints[iWait];

                TCSubpathRoute thisRoute = TCRoute.TCRouteSubpaths[waitingPoint[0]];
                int routeIndex = thisRoute.GetRouteIndex(waitingPoint[1], 0);
                if (iWait < TCRoute.WaitingPoints.Count - 1 && TCRoute.WaitingPoints[iWait + 1][1] == waitingPoint[1])
                    continue;
                int lastIndex = routeIndex;

                // check if waiting point is in route - else give warning and skip
                if (routeIndex < 0)
                {
                    Trace.TraceInformation("Waiting point for train " + Number.ToString() + " service " + Name + " is not on route - point removed");
                    continue;
                }

                int direction = thisRoute[routeIndex].Direction;
                bool endSectionFound = false;
                int endSignalIndex = -1;

                TrackCircuitSection thisSection = signalRef.TrackCircuitList[thisRoute[routeIndex].TCSectionIndex];
                TrackCircuitSection nextSection =
                    routeIndex < thisRoute.Count - 2 ? signalRef.TrackCircuitList[thisRoute[routeIndex + 1].TCSectionIndex] : null;

                if (thisSection.EndSignals[direction] != null)
                {
                    endSectionFound = true;
                    if (routeIndex < thisRoute.Count - 1)
                    endSignalIndex = thisSection.EndSignals[direction].thisRef;
                }

                // check if next section is junction

                else if (nextSection == null || nextSection.CircuitType != TrackCircuitSection.TrackCircuitType.Normal)
                {
                    endSectionFound = true;
                }

                // try and find next section with signal; if junction is found, stop search

                int nextIndex = routeIndex + 1;
                while (nextIndex < thisRoute.Count - 1 && !endSectionFound)
                {
                    nextSection = signalRef.TrackCircuitList[thisRoute[nextIndex].TCSectionIndex];
                    direction = thisRoute[nextIndex].Direction;

                    if (nextSection.EndSignals[direction] != null)
                    {
                        endSectionFound = true;
                        lastIndex = nextIndex;
                        if (lastIndex < thisRoute.Count - 1)
                        endSignalIndex = nextSection.EndSignals[direction].thisRef;
                    }
                    else if (nextSection.CircuitType != TrackCircuitSection.TrackCircuitType.Normal)
                    {
                        endSectionFound = true;
                        lastIndex = nextIndex - 1;
                    }
                    nextIndex++;
                }

                if (endSignalIndex > -1)
                {
                    AIActSigDelegateRef action = new AIActSigDelegateRef(this, Math.Max(waitingPoint[5]-1500, 0), 0f, waitingPoint[0], lastIndex, thisRoute[lastIndex].TCSectionIndex, direction);
                    signalRef.SignalObjects[endSignalIndex].LockForTrain(this.Number, waitingPoint[0]);
                    action.SetEndSignalIndex(endSignalIndex);
                    action.SetSignalObject(signalRef.SignalObjects[endSignalIndex]);
//                    action.Delay = waitingPoint[2] <= 5 ? 5 : waitingPoint[2];
                    action.Delay = waitingPoint[2];
                    if (waitingPoint[2] >= 30000 && waitingPoint[2] < 40000) action.IsAbsolute = true;
                    AuxActionsContain.Add(action);
                }
            }
        }

        //================================================================================================//
        /// <summary>
        /// in a certain % of cases depending from randomization level returns a 0 delay
        /// in the remainder of cases computes a randomized delay using a single-sided pseudo-gaussian distribution
        /// following Daniel Howard's suggestion here https://stackoverflow.com/questions/218060/random-gaussian-variables
        /// Parameters: 
        /// maxDelay maximum added random delay (may be seconds or minutes)
        /// </summary>Ac

        public int RandomizedDelayWithThreshold(int maxAddedDelay)
        {
            if (DateTime.Now.Millisecond % 10 < 6 - Simulator.Settings.ActRandomizationLevel) return 0;
            return (int)(Simulator.Random.Next(0, (int)(Simulator.Resolution * Simulator.Random.NextDouble()) + 1) / Simulator.Resolution * maxAddedDelay);
        }

        //================================================================================================//
        /// <summary>
        /// Computes a randomized delay using a single-sided pseudo-gaussian distribution
        /// following Daniel Howard's suggestion here https://stackoverflow.com/questions/218060/random-gaussian-variables
        /// Parameters: 
        /// maxDelay maximum added random delay (may be seconds or minutes)
        /// </summary>

        public int RandomizedDelay(int maxAddedDelay)
        {
            return (int)(Simulator.Random.Next(0, (int)(Simulator.Resolution * Simulator.Random.NextDouble()) + 1) / Simulator.Resolution * maxAddedDelay);
        }

        //================================================================================================//
        /// <summary>
        /// Computes a randomized delay for the various types of waiting points.
        /// </summary>

        public int RandomizedWPDelay(ref int randomizedDelay)
        {
            if (randomizedDelay < 30000) // standard WP
            {
                randomizedDelay += RandomizedDelayWithThreshold(15 + 5 * Simulator.Settings.ActRandomizationLevel);
            }
            else if (randomizedDelay >= 30000 && randomizedDelay < 40000) // absolute WP
            {
                randomizedDelay += RandomizedDelayWithThreshold(2 + Simulator.Settings.ActRandomizationLevel);
                if (randomizedDelay % 100 > 59)
                {
                    randomizedDelay += 40;
                    if ((randomizedDelay / 100) % 100 == 24) randomizedDelay -= 2400;
                }
            }
            else if (randomizedDelay > 40000 && randomizedDelay < 60000) // car detach WP
            {
                var additionalDelay = RandomizedDelayWithThreshold(25);
                if (randomizedDelay % 100 + additionalDelay > 99) randomizedDelay += 99;
                else randomizedDelay += additionalDelay;
            }
            return randomizedDelay;
        }

        //================================================================================================//
        /// <summary>
        /// Convert player traffic list to station list
        /// <\summary>

        public void ConvertPlayerTraffic(List<Player_Traffic_Item> playerList)
        {

            if (playerList == null || playerList.Count == 0)
            {
                return;    // no traffic details
            }

            TrafficService = new Traffic_Service_Definition();

            foreach (Player_Traffic_Item thisItem in playerList)
            {
                int iArrivalTime = Convert.ToInt32(thisItem.ArrivalTime.TimeOfDay.TotalSeconds);
                int iDepartTime = Convert.ToInt32(thisItem.DepartTime.TimeOfDay.TotalSeconds);
                Traffic_Traffic_Item newItem = new Traffic_Traffic_Item(iArrivalTime, iDepartTime,
                        0, thisItem.DistanceDownPath, thisItem.PlatformStartID);
                TrafficService.TrafficDetails.Add(newItem);
            }

            BuildStationList(15.0f);  // use 15m. clearing distance
        }

        //================================================================================================//
        /// <summary>
        /// Clear station from list, clear exit signal if required
        /// <\summary>

        public virtual void ClearStation(uint id1, uint id2, bool removeStation)
        {
            int foundStation = -1;
            StationStop thisStation = null;

            for (int iStation = 0; iStation < StationStops.Count && foundStation < 0; iStation++)
            {
                thisStation = StationStops[iStation];
                if (thisStation.SubrouteIndex > TCRoute.activeSubpath) break;
                if (thisStation.PlatformReference == id1 ||
                    thisStation.PlatformReference == id2)
                {
                    foundStation = iStation;
                }

                if (thisStation.SubrouteIndex > TCRoute.activeSubpath) break; // stop looking if station is in next subpath
            }

            if (foundStation >= 0)
            {
                thisStation = StationStops[foundStation];
                if (thisStation.ExitSignal >= 0)
                {
                    HoldingSignals.Remove(thisStation.ExitSignal);

                    if (ControlMode == TRAIN_CONTROL.AUTO_SIGNAL)
                    {
                        SignalObject nextSignal = signalRef.SignalObjects[thisStation.ExitSignal];
                        nextSignal.requestClearSignal(ValidRoute[0], routedForward, 0, false, null);
                    }
                }
            }
            if (removeStation)
            {
                for (int iStation = foundStation; iStation >= 0; iStation--)
                {
                    PreviousStop = StationStops[iStation].CreateCopy();
                    StationStops.RemoveAt(iStation);
                }
            }
        }

        /// <summary>
        /// Create pathless player train out of static train
        /// </summary>

        public void CreatePathlessPlayerTrain()
        {
            TrainType = Train.TRAINTYPE.PLAYER;
            IsPathless = true;
            CheckFreight();
            ToggleToManualMode();
            InitializeBrakes();
            InitializeSpeeds();
        }

        /// <summary>
        /// Initializes speeds for pathless player train
        /// </summary>
        ///

        public void InitializeSpeeds()
        {
            allowedMaxSpeedSignalMpS = allowedAbsoluteMaxSpeedSignalMpS;
            allowedMaxSpeedLimitMpS = allowedAbsoluteMaxSpeedLimitMpS;
            allowedMaxTempSpeedLimitMpS = allowedAbsoluteMaxTempSpeedLimitMpS;
            TrainMaxSpeedMpS = Math.Min((float)Simulator.TRK.Tr_RouteFile.SpeedLimit, ((MSTSLocomotive)Simulator.PlayerLocomotive).MaxSpeedMpS);
        }

        /// <summary>
        /// Gets the train name from one CarID; used for remote trains
        /// </summary>
        ///

        public string GetTrainName(string ID)
        {
            int location = ID.LastIndexOf('-');
            if (location < 0) return ID;
            return ID.Substring(0, location - 1);
        }

        //================================================================================================//

        /// <summary>
        /// Create status line
        /// <\summary>
        /// <remarks>
        ///  "Train", "Travelled", "Speed", "Max", "AI mode", "AI data", "Mode", "Auth", "Distance", "Signal", "Distance", "Consist", "Path"
        ///  0   Train: Number with trailing type (F freight, P Passenger)
        ///  1   Travelled: travelled distance so far
        ///  2   Speed: Current speed
        ///  3   Max: Maximum allowed speed
        ///  4   AIMode :
        ///      INI     : AI is in INIT mode
        ///      STC     : AI is static
        ///      STP     : AI is Stopped
        ///      BRK     : AI Brakes
        ///      ACC     : AI do acceleration
        ///      FOL     : AI follows
        ///      RUN     : AI is running
        ///      EOP     : AI approch and of path
        ///      STA     : AI is on Station Stop
        ///      WTP     : AI is on Waiting Point
        ///      STE     : AI is in Stopped Existing state
        ///  5   AI Data :
        ///      000&000     : Throttel & Brake in %
        ///                  : for mode INI, BRK, ACC, FOL, RUN or EOP
        ///      HH:mm:ss    : for mode STA or WTP with actualDepart or DepartTime
        ///                  : for mode STC with Start Time Value
        ///      ..:..:..    : For other case
        ///  6   Mode:
        ///          SIGN or Sdelay: Train in AUTO_SIGNAL, with delay if train delayed
        ///          NODE or Ndelay: Train in AUTO_NODE, with delay if train delayed
        ///          MAN: Train in AUTO_MANUAL
        ///          OOC: Train in OUT_OF_CONTROL
        ///          EXP: Train in EXPLORER
        ///  7   Auth + Distance:    For Player Train
        ///          case OOC:   Distance set to blank
        ///              SPAD:   Signal Passed At Danger
        ///              RSPD:   Rear SPAD
        ///              OOAU:   Out Of Authority
        ///              OOPA:   Out Of Path
        ///              SLPP:   Slipped out Path
        ///              SLPT:   Slipped to End of Track
        ///              OOTR:   To End Of Track
        ///              MASW:   Misaligned Switch
        ///              ....:   Undefined
        ///          case Waiting Point: WAIT, Distance set to Train Number to Wait ????
        ///          case NODE:                      Distance: Blank or
        ///              EOT:    End Of Track
        ///              EOP:    End Of Path
        ///              RSW:    Reserved Switch
        ///              LP:     Loop
        ///              TAH:    Train Ahead
        ///              MXD:    Max Distance        Distance: To End Of Authority
        ///              NOP:    No Path Reserved    Distance: To End Of Authority
        ///              ...:    Undefined
        ///          Other:
        ///              Blank + Blank
        ///  7   Next Action :   For AI Train
        ///              SPDL    :   Speed limit
        ///              SIGL    :   Speed signal
        ///              STOP    :   Signal STOP
        ///              REST    :   Signal RESTRICTED
        ///              EOA     :   End Of Authority
        ///              STAT    :   Station Stop
        ///              TRAH    :   Train Ahead
        ///              EOR     :   End Of Route
        ///              NONE    :   None
        ///  9   Signal + Distance
        ///          Manual or Explorer: Distance set to blank
        ///              First:  Reverse direction
        ///                  G:  Signal at STOP but Permission Granted
        ///                  S:  Signal At STOP
        ///                  P:  Signal at STOP & PROCEED
        ///                  R:  Signal at RESTRICTING
        ///                  A:  Signal at APPROACH 1, 2 or 3
        ///                  C:  Signal at CLEAR 1 or 2
        ///                  -:  Not Defined
        ///              <>
        ///              Second: Forward direction
        ///                  G:  Signal at STOP but Permission Granted
        ///                  S:  Signal At STOP
        ///                  P:  Signal at STOP & PROCEED
        ///                  R:  Signal at RESTRICTING
        ///                  A:  Signal at APPROACH 1, 2 or 3
        ///                  C:  Signal at CLEAR 1 or 2
        ///                  -:  Not Defined
        ///          Other:  Distance is Distance to next Signal
        ///              STOP:   Signal at STOP
        ///              SPRC:   Signal at STOP & PROCEED
        ///              REST:   Signal at RESTRICTING
        ///              APP1:   Signal at APPROACH 1
        ///              APP2:   Signal at APPROACH 2
        ///              APP3:   Signal at APPROACH 3
        ///              CLR1:   Signal at CLEAR 1
        ///              CLR2:   Signal at CLEAR 2
        ///  10  Consist:
        ///          PLAYER:
        ///          REMOTE:
        ///  11  Path:
        ///          not Manual nor Explorer:
        ///              number or ?     :   Id of subpath in valid TCRoute or ? if no valid TCRoute
        ///              =[n]            :   Number of remaining station stops
        ///              {               :   Starting String
        ///              CircuitString   :   List of Circuit (see next)
        ///              }               :   Ending String
        ///              x or blank      :   x if already on TCRoute
        ///          Manual or Explorer:
        ///              CircuitString   :   Backward
        ///              ={  Dir }=      :   Dir is '<' or '>'
        ///              CircuitString   :   Forward
        ///          For AI  :
        ///              Train Name
        ///  
        ///      CircuitString analyse:
        ///          Build string for section information
        ///      returnString +
        ///      CircuitType:
        ///          >   : Junction
        ///          +   : CrossOver
        ///          [   : End of Track direction 1
        ///          ]   : End of Track direction 0
        ///          -   : Default (Track Section)
        ///      Deadlock traps:
        ///          Yes : Ended with *
        ///              Await number    : ^
        ///              Await more      : ~
        ///      Train Occupancy:    + '&' If more than one
        ///          N° of train     : If one train
        ///      If train reservation :
        ///          (
        ///          Train Number
        ///          )
        ///      If signal reserved :
        ///          (S
        ///          Signal Number
        ///          )
        ///      If one or more train claim
        ///          #
        /// <\remarks>
        public String[] GetStatus(bool metric)
        {

            int iColumn = 0;

            string[] statusString = new string[13];

            //  0, "Train"
            statusString[iColumn] = Number.ToString();

            if (Delay.HasValue && Delay.Value.TotalMinutes >= 1)
            {
                statusString[iColumn] = String.Concat(statusString[iColumn], " D");
            }
            else if (IsFreight)
            {
                statusString[iColumn] = String.Concat(statusString[iColumn], " F");
            }
            else
            {
                statusString[iColumn] = String.Concat(statusString[iColumn], " P");
            }
            iColumn++;

            //  1, "Travelled"
            statusString[iColumn] = FormatStrings.FormatDistanceDisplay(DistanceTravelledM, metric);
            iColumn++;
            //  2, "Speed"
            var trainSpeed = TrainType == Train.TRAINTYPE.REMOTE && SpeedMpS != 0 ? targetSpeedMpS : SpeedMpS;
            statusString[iColumn] = FormatStrings.FormatSpeed(trainSpeed, metric);
            if (Math.Abs(trainSpeed) > Math.Abs(AllowedMaxSpeedMpS)) statusString[iColumn] += "!!!";
            iColumn++;
            //  3, "Max"
            statusString[iColumn] = FormatStrings.FormatSpeedLimit(AllowedMaxSpeedMpS, metric);
            iColumn++;

            //  4, "AI mode"
            statusString[iColumn] = " ";  // for AI trains
            iColumn++;
            //  5, "AI data"
            statusString[iColumn] = " ";  // for AI trains
            iColumn++;

            //  6, "Mode"
            switch (ControlMode)
            {
                case TRAIN_CONTROL.AUTO_SIGNAL:
                    if (Delay.HasValue)
                    {
                        statusString[iColumn] = String.Concat("S +", Delay.Value.TotalMinutes.ToString("00"));
                    }
                    else
                    {
                        statusString[iColumn] = "SIGN";
                    }
                    break;
                case TRAIN_CONTROL.AUTO_NODE:
                    if (Delay.HasValue)
                    {
                        statusString[iColumn] = String.Concat("N +", Delay.Value.TotalMinutes.ToString("00"));
                    }
                    else
                    {
                        statusString[iColumn] = "NODE";
                    }
                    break;
                case TRAIN_CONTROL.MANUAL:
                    statusString[iColumn] = "MAN";
                    break;
                case TRAIN_CONTROL.OUT_OF_CONTROL:
                    statusString[iColumn] = "OOC";
                    break;
                case TRAIN_CONTROL.EXPLORER:
                    statusString[iColumn] = "EXPL";
                    break;
                case TRAIN_CONTROL.TURNTABLE:
                    statusString[iColumn] = "TURN";
                    break;
                default:
                    statusString[iColumn] = "----";
                    break;
            }

            iColumn++;
            //  7, "Auth"
            if (ControlMode == TRAIN_CONTROL.OUT_OF_CONTROL)
            {
                switch (OutOfControlReason)
                {
                    case OUTOFCONTROL.SPAD:
                        statusString[iColumn] = "SPAD";
                        break;
                    case OUTOFCONTROL.SPAD_REAR:
                        statusString[iColumn] = "RSPD";
                        break;
                    case OUTOFCONTROL.OUT_OF_AUTHORITY:
                        statusString[iColumn] = "OOAU";
                        break;
                    case OUTOFCONTROL.OUT_OF_PATH:
                        statusString[iColumn] = "OOPA";
                        break;
                    case OUTOFCONTROL.SLIPPED_INTO_PATH:
                        statusString[iColumn] = "SLPP";
                        break;
                    case OUTOFCONTROL.SLIPPED_TO_ENDOFTRACK:
                        statusString[iColumn] = "SLPT";
                        break;
                    case OUTOFCONTROL.OUT_OF_TRACK:
                        statusString[iColumn] = "OOTR";
                        break;
                    case OUTOFCONTROL.MISALIGNED_SWITCH:
                        statusString[iColumn] = "MASW";
                        break;
                    case OUTOFCONTROL.SLIPPED_INTO_TURNTABLE:
                        statusString[iColumn] = "SLPT";
                        break;
                    default:
                        statusString[iColumn] = "....";
                        break;
                }

                iColumn++;
                //  8, "Distance"
                statusString[iColumn] = " ";
            }

            else if (ControlMode == TRAIN_CONTROL.AUTO_NODE)
            {
                switch (EndAuthorityType[0])
                {
                    case END_AUTHORITY.END_OF_TRACK:
                        statusString[iColumn] = "EOT";
                        break;
                    case END_AUTHORITY.END_OF_PATH:
                        statusString[iColumn] = "EOP";
                        break;
                    case END_AUTHORITY.RESERVED_SWITCH:
                        statusString[iColumn] = "RSW";
                        break;
                    case END_AUTHORITY.LOOP:
                        statusString[iColumn] = "LP ";
                        break;
                    case END_AUTHORITY.TRAIN_AHEAD:
                        statusString[iColumn] = "TAH";
                        break;
                    case END_AUTHORITY.MAX_DISTANCE:
                        statusString[iColumn] = "MXD";
                        break;
                    case END_AUTHORITY.NO_PATH_RESERVED:
                        statusString[iColumn] = "NOP";
                        break;
                    default:
                        statusString[iColumn] = "";
                        break;
                }

                iColumn++;
                //  8, "Distance"
                if (EndAuthorityType[0] != END_AUTHORITY.MAX_DISTANCE && EndAuthorityType[0] != END_AUTHORITY.NO_PATH_RESERVED)
                {
                    statusString[iColumn] = FormatStrings.FormatDistance(DistanceToEndNodeAuthorityM[0], metric);
                }
                else
                {
                    statusString[iColumn] = " ";
                }
            }
            else
            {
                statusString[iColumn] = " ";
                iColumn++;
                //  8, "Distance"
                statusString[iColumn] = " ";
            }

            iColumn++;
            //  9, "Signal"
            if (ControlMode == TRAIN_CONTROL.MANUAL || ControlMode == TRAIN_CONTROL.EXPLORER)
            {
                // reverse direction
                string firstchar = "-";

                if (NextSignalObject[1] != null)
                {
                    MstsSignalAspect nextAspect = GetNextSignalAspect(1);
                    if (NextSignalObject[1].enabledTrain == null || NextSignalObject[1].enabledTrain.Train != this) nextAspect = MstsSignalAspect.STOP;  // aspect only valid if signal enabled for this train

                    switch (nextAspect)
                    {
                        case MstsSignalAspect.STOP:
                            if (NextSignalObject[1].hasPermission == SignalObject.Permission.Granted)
                            {
                                firstchar = "G";
                            }
                            else
                            {
                                firstchar = "S";
                            }
                            break;
                        case MstsSignalAspect.STOP_AND_PROCEED:
                            firstchar = "P";
                            break;
                        case MstsSignalAspect.RESTRICTING:
                            firstchar = "R";
                            break;
                        case MstsSignalAspect.APPROACH_1:
                            firstchar = "A";
                            break;
                        case MstsSignalAspect.APPROACH_2:
                            firstchar = "A";
                            break;
                        case MstsSignalAspect.APPROACH_3:
                            firstchar = "A";
                            break;
                        case MstsSignalAspect.CLEAR_1:
                            firstchar = "C";
                            break;
                        case MstsSignalAspect.CLEAR_2:
                            firstchar = "C";
                            break;
                    }
                }

                // forward direction
                string lastchar = "-";

                if (NextSignalObject[0] != null)
                {
                    MstsSignalAspect nextAspect = GetNextSignalAspect(0);
                    if (NextSignalObject[0].enabledTrain == null || NextSignalObject[0].enabledTrain.Train != this) nextAspect = MstsSignalAspect.STOP;  // aspect only valid if signal enabled for this train

                    switch (nextAspect)
                    {
                        case MstsSignalAspect.STOP:
                            if (NextSignalObject[0].hasPermission == SignalObject.Permission.Granted)
                            {
                                lastchar = "G";
                            }
                            else
                            {
                                lastchar = "S";
                            }
                            break;
                        case MstsSignalAspect.STOP_AND_PROCEED:
                            lastchar = "P";
                            break;
                        case MstsSignalAspect.RESTRICTING:
                            lastchar = "R";
                            break;
                        case MstsSignalAspect.APPROACH_1:
                            lastchar = "A";
                            break;
                        case MstsSignalAspect.APPROACH_2:
                            lastchar = "A";
                            break;
                        case MstsSignalAspect.APPROACH_3:
                            lastchar = "A";
                            break;
                        case MstsSignalAspect.CLEAR_1:
                            lastchar = "C";
                            break;
                        case MstsSignalAspect.CLEAR_2:
                            lastchar = "C";
                            break;
                    }
                }

                statusString[iColumn] = String.Concat(firstchar, "<>", lastchar);
                iColumn++;
                //  9, "Distance"
                statusString[iColumn] = " ";
            }
            else
            {
                if (NextSignalObject[0] != null)
                {
                    MstsSignalAspect nextAspect = GetNextSignalAspect(0);

                    switch (nextAspect)
                    {
                        case MstsSignalAspect.STOP:
                            statusString[iColumn] = "STOP";
                            break;
                        case MstsSignalAspect.STOP_AND_PROCEED:
                            statusString[iColumn] = "SPRC";
                            break;
                        case MstsSignalAspect.RESTRICTING:
                            statusString[iColumn] = "REST";
                            break;
                        case MstsSignalAspect.APPROACH_1:
                            statusString[iColumn] = "APP1";
                            break;
                        case MstsSignalAspect.APPROACH_2:
                            statusString[iColumn] = "APP2";
                            break;
                        case MstsSignalAspect.APPROACH_3:
                            statusString[iColumn] = "APP3";
                            break;
                        case MstsSignalAspect.CLEAR_1:
                            statusString[iColumn] = "CLR1";
                            break;
                        case MstsSignalAspect.CLEAR_2:
                            statusString[iColumn] = "CLR2";
                            break;
                    }

                    iColumn++;
                    //  9, "Distance"
                    if (DistanceToSignal.HasValue)
                    {
                        statusString[iColumn] = FormatStrings.FormatDistance(DistanceToSignal.Value, metric);
                    }
                    else
                    {
                        statusString[iColumn] = "-";
                    }
                }
                else
                {
                    statusString[iColumn] = " ";
                    iColumn++;
                    //  9, "Distance"
                    statusString[iColumn] = " ";
                }
            }

            iColumn++;
            //  10, "Consist"
            statusString[iColumn] = "PLAYER";
            if (!Simulator.TimetableMode && this != Simulator.OriginalPlayerTrain) statusString[iColumn] = Name.Substring(0, Math.Min(Name.Length, 7));
            if (TrainType == TRAINTYPE.REMOTE)
            {
                var trainName = "";
                if (LeadLocomotive != null) trainName = GetTrainName(LeadLocomotive.CarID);
                else if (Cars != null && Cars.Count > 0) trainName = GetTrainName(Cars[0].CarID);
                else trainName = "REMOTE";
                statusString[iColumn] = trainName.Substring(0, Math.Min(trainName.Length, 7));
            }

            iColumn++;
            //  11, "Path"
            string circuitString = String.Empty;

            if ((ControlMode != TRAIN_CONTROL.MANUAL && ControlMode != TRAIN_CONTROL.EXPLORER) || ValidRoute[1] == null)
            {
                // station stops
                if (StationStops == null || StationStops.Count == 0)
                {
                    circuitString = string.Concat(circuitString, "[ ] ");
                }
                else
                {
                    circuitString = string.Concat(circuitString, "[", StationStops.Count, "] ");
                }

                // route
                if (TCRoute == null)
                {
                    circuitString = string.Concat(circuitString, "?={");
                }
                else
                {
                    circuitString = String.Concat(circuitString, TCRoute.activeSubpath.ToString());
                    circuitString = String.Concat(circuitString, "={");
                }

                int startIndex = PresentPosition[0].RouteListIndex;
                if (startIndex < 0)
                {
                    circuitString = String.Concat(circuitString, "<out of route>");
                }
                else
                {
                    for (int iIndex = PresentPosition[0].RouteListIndex; iIndex < ValidRoute[0].Count; iIndex++)
                    {
                        TCRouteElement thisElement = ValidRoute[0][iIndex];
                        TrackCircuitSection thisSection = signalRef.TrackCircuitList[thisElement.TCSectionIndex];

                        circuitString = BuildSectionString(circuitString, thisSection, 0);

                    }
                }

                circuitString = String.Concat(circuitString, "}");

                if (TCRoute != null && TCRoute.activeSubpath < TCRoute.TCRouteSubpaths.Count - 1)
                {
                    circuitString = String.Concat(circuitString, "x", (TCRoute.activeSubpath + 1).ToString());
                }
                if (TCRoute != null && TCRoute.OriginalSubpath != -1) circuitString += "???";
            }
            else
            {
                // backward path
                string backstring = String.Empty;
                for (int iindex = ValidRoute[1].Count - 1; iindex >= 0; iindex--)
                {
                    TrackCircuitSection thisSection = signalRef.TrackCircuitList[ValidRoute[1][iindex].TCSectionIndex];
                    backstring = BuildSectionString(backstring, thisSection, 1);
                }

                if (backstring.Length > 30)
                {
                    backstring = backstring.Substring(backstring.Length - 30);
                    // ensure string starts with section delimiter
                    while (String.Compare(backstring.Substring(0, 1), "-") != 0 &&
                           String.Compare(backstring.Substring(0, 1), "+") != 0 &&
                           String.Compare(backstring.Substring(0, 1), "<") != 0)
                    {
                        backstring = backstring.Substring(1);
                    }

                    circuitString = String.Concat(circuitString, "...");
                }
                circuitString = String.Concat(circuitString, backstring);

                // train indication and direction
                circuitString = String.Concat(circuitString, "={");
                if (MUDirection == Direction.Reverse)
                {
                    circuitString = String.Concat(circuitString, "<");
                }
                else
                {
                    circuitString = String.Concat(circuitString, ">");
                }
                circuitString = String.Concat(circuitString, "}=");

                // forward path

                string forwardstring = String.Empty;
                for (int iindex = 0; iindex < ValidRoute[0].Count; iindex++)
                {
                    TrackCircuitSection thisSection = signalRef.TrackCircuitList[ValidRoute[0][iindex].TCSectionIndex];
                    forwardstring = BuildSectionString(forwardstring, thisSection, 0);
                }
                circuitString = String.Concat(circuitString, forwardstring);
            }

            statusString[iColumn] = String.Copy(circuitString);

            return (statusString);
        }

        //================================================================================================//


        /// <summary>
        ///  Build string for section information
        ///  <c>returnString +
        ///     CircuitType:
        ///         >   : Junction
        ///         +   : CrossOver
        ///         [   : End of Track direction 1
        ///         ]   : End of Track direction 0
        ///     Deadlock traps:
        ///         Yes : Ended with *
        ///             Await number    : ^
        ///             Await more      : ~
        ///     Train Occupancy:    + '&' If more than one
        ///         N° of train     : If one train
        ///     If train reservation :
        ///         (
        ///         Train Number
        ///         )
        ///     If signal reserved :
        ///         (S
        ///         Signal Number
        ///         )
        ///     If one or more train claim
        ///         #</c>
        /// </summary>
        public string BuildSectionString(string thisString, TrackCircuitSection thisSection, int direction)
        {

            string returnString = String.Copy(thisString);

            switch (thisSection.CircuitType)
            {
                case TrackCircuitSection.TrackCircuitType.Junction:
                    returnString = String.Concat(returnString, ">");
                    break;
                case TrackCircuitSection.TrackCircuitType.Crossover:
                    returnString = String.Concat(returnString, "+");
                    break;
                case TrackCircuitSection.TrackCircuitType.EndOfTrack:
                    returnString = direction == 0 ? String.Concat(returnString, "]") : String.Concat(returnString, "[");
                    break;
                default:
                    returnString = String.Concat(returnString, "-");
                    break;
            }

            if (thisSection.DeadlockTraps.ContainsKey(Number))
            {
                if (thisSection.DeadlockAwaited.Contains(Number))
                {
                    returnString = String.Concat(returnString, "^[");
                    List<int> deadlockInfo = thisSection.DeadlockTraps[Number];
                    for (int index = 0; index < deadlockInfo.Count - 2; index++)
                    {
                        returnString = String.Concat(returnString, deadlockInfo[index].ToString(), ",");
                    }
                    returnString = String.Concat(returnString, deadlockInfo.Last().ToString(), "]");
                }
                else if (thisSection.DeadlockAwaited.Count > 0)
                {
                    returnString = String.Concat(returnString, "~");
                }
                returnString = String.Concat(returnString, "*");
            }

            if (thisSection.CircuitState.TrainOccupy.Count > 0)
            {
                List<TrainRouted> allTrains = thisSection.CircuitState.TrainsOccupying();
                int trainno = allTrains[0].Train.Number;
                returnString = String.Concat(returnString, trainno.ToString());
                if (allTrains.Count > 1)
                {
                    returnString = String.Concat(returnString, "&");
                }
            }

            if (thisSection.CircuitState.TrainReserved != null)
            {
                int trainno = thisSection.CircuitState.TrainReserved.Train.Number;
                returnString = String.Concat(returnString, "(", trainno.ToString(), ")");
            }

            if (thisSection.CircuitState.SignalReserved >= 0)
            {
                returnString = String.Concat(returnString, "(S", thisSection.CircuitState.SignalReserved.ToString(), ")");
            }

            if (thisSection.CircuitState.TrainClaimed.Count > 0)
            {
                returnString = String.Concat(returnString, "#");
            }

            return (returnString);
        }

#if WITH_PATH_DEBUG
        //================================================================================================//
        /// <summary>
        /// Create Path information line
        /// "Train", "Path"
        /// <\summary>

        public String[] GetPathStatus(bool metric)
        {
            int iColumn = 0;

            string[] statusString = new string[5];

            //  "Train"
            statusString[0] = Number.ToString();
            iColumn++;

            //  "Action"
            statusString[1] = "----";
            statusString[2] = "..";
            iColumn = 3;

            string circuitString = String.Empty;
            circuitString = string.Concat(circuitString, "Path: ");


            statusString[iColumn] = String.Copy(circuitString);
            iColumn++;

            return (statusString);

        }
#endif

        //================================================================================================//
        /// <summary>
        /// Add restart times at stations and waiting points
        /// Update the string for 'TextPageDispatcherInfo'.
        /// Modifiy fields 4 and 5
        /// <\summary>

        public String[] AddRestartTime(String[] stateString)
        {
            String[] retString = new String[stateString.Length];
            stateString.CopyTo(retString, 0);

            string movString = "";
            string abString = "";
            DateTime baseDT = new DateTime();
            if (this == Simulator.OriginalPlayerTrain)
            {
                if (Simulator.ActivityRun != null && Simulator.ActivityRun.Current is ActivityTaskPassengerStopAt && ((ActivityTaskPassengerStopAt)Simulator.ActivityRun.Current).BoardingS > 0)
                {
                    movString = "STA";
                    DateTime depTime = baseDT.AddSeconds(((ActivityTaskPassengerStopAt)Simulator.ActivityRun.Current).BoardingEndS);
                    abString = depTime.ToString("HH:mm:ss");
                }
                else
                   if (Math.Abs(SpeedMpS) <= 0.01 && AuxActionsContain.specRequiredActions.Count > 0 && AuxActionsContain.specRequiredActions.First.Value is AuxActSigDelegate &&
                    (AuxActionsContain.specRequiredActions.First.Value as AuxActSigDelegate).currentMvmtState == AITrain.AI_MOVEMENT_STATE.HANDLE_ACTION)
                {
                    movString = "WTS";
                    DateTime depTime = baseDT.AddSeconds((AuxActionsContain.specRequiredActions.First.Value as AuxActSigDelegate).ActualDepart);
                    abString = depTime.ToString("HH:mm:ss");
                }
            }
            else if (StationStops.Count > 0 && AtStation)
            {
                movString = "STA";
                if (StationStops[0].ActualDepart > 0)
                {
                    DateTime depTime = baseDT.AddSeconds(StationStops[0].ActualDepart);
                    abString = depTime.ToString("HH:mm:ss");
                }
                else
                {
                    abString = "..:..:..";
                }
            }
            else if (Math.Abs(SpeedMpS) <= 0.01 && (this as AITrain).nextActionInfo is AuxActionWPItem &&
                    (this as AITrain).MovementState == AITrain.AI_MOVEMENT_STATE.HANDLE_ACTION)
            {
                movString = "WTP";
                DateTime depTime = baseDT.AddSeconds(((this as AITrain).nextActionInfo as AuxActionWPItem).ActualDepart);
                abString = depTime.ToString("HH:mm:ss");
            }
            else if (Math.Abs(SpeedMpS) <= 0.01 && AuxActionsContain.SpecAuxActions.Count > 0 && AuxActionsContain.SpecAuxActions[0] is AIActionWPRef &&
                (AuxActionsContain.SpecAuxActions[0] as AIActionWPRef).keepIt != null &&
                (AuxActionsContain.SpecAuxActions[0] as AIActionWPRef).keepIt.currentMvmtState == AITrain.AI_MOVEMENT_STATE.HANDLE_ACTION)
            {
                movString = "WTP";
                DateTime depTime = baseDT.AddSeconds((AuxActionsContain.SpecAuxActions[0] as AIActionWPRef).keepIt.ActualDepart);
                abString = depTime.ToString("HH:mm:ss");
            }
            retString[4] = String.Copy(movString);
            retString[5] = String.Copy(abString);

            return (retString);
        }


        //================================================================================================//
        /// <summary>
        /// Create TrackInfoObject for information in TrackMonitor window
        /// </summary>

        public TrainInfo GetTrainInfo()
        {
            TrainInfo thisInfo = new TrainInfo();

            if (ControlMode == TRAIN_CONTROL.AUTO_NODE || ControlMode == TRAIN_CONTROL.AUTO_SIGNAL)
            {
                GetTrainInfoAuto(ref thisInfo);
            }
            else if (ControlMode == TRAIN_CONTROL.MANUAL || ControlMode == TRAIN_CONTROL.EXPLORER)
            {
                GetTrainInfoManual(ref thisInfo);
            }
            else if (ControlMode == TRAIN_CONTROL.OUT_OF_CONTROL)
            {
                GetTrainInfoOOC(ref thisInfo);
            }
            else // no state? should not occur, but just set no details at all
            {
                thisInfo.ControlMode = ControlMode;
                thisInfo.direction = 0;
                thisInfo.speedMpS = 0;
                TrainObjectItem dummyItem = new TrainObjectItem(END_AUTHORITY.NO_PATH_RESERVED, 0.0f);
                thisInfo.ObjectInfoForward.Add(dummyItem);
                thisInfo.ObjectInfoBackward.Add(dummyItem);
            }

            // sort items on increasing distance

            thisInfo.ObjectInfoForward.Sort();
            thisInfo.ObjectInfoBackward.Sort();

            return (thisInfo);
        }

        //================================================================================================//
        /// <summary>
        /// Create TrackInfoObject for information in TrackMonitor window for Auto mode
        /// </summary>

        public void GetTrainInfoAuto(ref TrainInfo thisInfo)
        {
            // set control modes
            thisInfo.ControlMode = ControlMode;

            // set speed
            thisInfo.speedMpS = SpeedMpS;

            // set projected speed
            thisInfo.projectedSpeedMpS = ProjectedSpeedMpS;

            // set max speed
            thisInfo.allowedSpeedMpS = Math.Min(AllowedMaxSpeedMpS, TrainMaxSpeedMpS);

            // set gradient
            thisInfo.currentElevationPercent = Simulator.PlayerLocomotive != null ? Simulator.PlayerLocomotive.CurrentElevationPercent : 0;

            // set direction
            thisInfo.direction = MUDirection == Direction.Forward ? 0 : (MUDirection == Direction.Reverse ? 1 : -1);

            // set orientation
            thisInfo.cabOrientation = Simulator.PlayerLocomotive != null ? ((Simulator.PlayerLocomotive.Flipped ^ Simulator.PlayerLocomotive.GetCabFlipped()) ? 1 : 0) : 0;

            // set reversal point

            TCReversalInfo thisReversal = TCRoute.ReversalInfo[TCRoute.activeSubpath];
            AddTrainReversalInfo(thisReversal, ref thisInfo);

            // set waiting point
            if (this != Simulator.OriginalPlayerTrain)
                AddWaitingPointInfo(ref thisInfo);

            bool maxAuthSet = false;
            // set object items - forward
            if (ControlMode == TRAIN_CONTROL.AUTO_NODE)
            {
                TrainObjectItem nextItem = new TrainObjectItem(EndAuthorityType[0], DistanceToEndNodeAuthorityM[0]);
                thisInfo.ObjectInfoForward.Add(nextItem);
                maxAuthSet = true;
            }

            bool signalProcessed = false;
            foreach (ObjectItemInfo thisItem in SignalObjectItems)
            {
                if (thisItem.ObjectType == ObjectItemInfo.ObjectItemType.Signal)
                {
                    TrackMonitorSignalAspect signalAspect =
                        thisItem.ObjectDetails.TranslateTMAspect(thisItem.ObjectDetails.this_sig_lr(MstsSignalFunction.NORMAL));
                    if (thisItem.ObjectDetails.enabledTrain == null || thisItem.ObjectDetails.enabledTrain.Train != this)
                    {
                        signalAspect = TrackMonitorSignalAspect.Stop;
                        TrainObjectItem stopItem = new TrainObjectItem(signalAspect,
                             thisItem.actual_speed, thisItem.distance_to_train);
                        thisInfo.ObjectInfoForward.Add(stopItem);
                        signalProcessed = true;
                        break;
                    }
                    TrainObjectItem nextItem = new TrainObjectItem(signalAspect,
                         thisItem.actual_speed, thisItem.distance_to_train);
                    thisInfo.ObjectInfoForward.Add(nextItem);
                    signalProcessed = true;
                }
                else if (thisItem.ObjectType == ObjectItemInfo.ObjectItemType.Speedlimit && thisItem.actual_speed > 0)
                {
                    TrainObjectItem nextItem = new TrainObjectItem(thisItem.actual_speed, thisItem.distance_to_train,
                        (TrainObjectItem.SpeedItemType)(thisItem.speed_noSpeedReductionOrIsTempSpeedReduction));
                    thisInfo.ObjectInfoForward.Add(nextItem);
                }
            }

            if (!signalProcessed && NextSignalObject[0] != null && NextSignalObject[0].enabledTrain != null && NextSignalObject[0].enabledTrain.Train == this)
            {
                TrackMonitorSignalAspect signalAspect =
                    NextSignalObject[0].TranslateTMAspect(NextSignalObject[0].this_sig_lr(MstsSignalFunction.NORMAL));
                ObjectSpeedInfo thisSpeedInfo = NextSignalObject[0].this_sig_speed(MstsSignalFunction.NORMAL);
                float validSpeed = thisSpeedInfo == null ? -1 : (IsFreight ? thisSpeedInfo.speed_freight : thisSpeedInfo.speed_pass);

                TrainObjectItem nextItem = new TrainObjectItem(signalAspect, validSpeed, DistanceToSignal);
                thisInfo.ObjectInfoForward.Add(nextItem);
            }

            if (StationStops != null && StationStops.Count > 0 &&
                (!maxAuthSet || StationStops[0].DistanceToTrainM < DistanceToEndNodeAuthorityM[0]) &&
                StationStops[0].SubrouteIndex == TCRoute.activeSubpath)
             {
                TrainObjectItem nextItem = new TrainObjectItem(StationStops[0].DistanceToTrainM, (int)StationStops[0].PlatformItem.Length);
                thisInfo.ObjectInfoForward.Add(nextItem);
             }            


            // Draft to display more station stops
            /*            if (StationStops != null && StationStops.Count > 0)
            {
                for (int iStation = 0; iStation < StationStops.Count; iStation++)
                {
                    if ((!maxAuthSet || StationStops[iStation].DistanceToTrainM <= DistanceToEndNodeAuthorityM[0]) && StationStops[iStation].SubrouteIndex == TCRoute.activeSubpath)
                    {
                        TrainObjectItem nextItem = new TrainObjectItem(StationStops[iStation].DistanceToTrainM, (int)StationStops[iStation].PlatformItem.Length);
                        thisInfo.ObjectInfoForward.Add(nextItem);
                    }
                    else break;
                }
            }*/

            // run along forward path to catch all diverging switches and mileposts

            AddSwitch_MilepostInfo(ref thisInfo, 0);

             // set object items - backward

            if (ClearanceAtRearM <= 0)
            {
                TrainObjectItem nextItem = new TrainObjectItem(END_AUTHORITY.NO_PATH_RESERVED, 0.0f);
                thisInfo.ObjectInfoBackward.Add(nextItem);
            }
            else
            {
                if (RearSignalObject != null)
                {
                    TrackMonitorSignalAspect signalAspect = RearSignalObject.TranslateTMAspect(RearSignalObject.this_sig_lr(MstsSignalFunction.NORMAL));
                    TrainObjectItem nextItem = new TrainObjectItem(signalAspect, -1.0f, ClearanceAtRearM);
                    thisInfo.ObjectInfoBackward.Add(nextItem);
                }
                else
                {
                    TrainObjectItem nextItem = new TrainObjectItem(END_AUTHORITY.END_OF_AUTHORITY, ClearanceAtRearM);
                    thisInfo.ObjectInfoBackward.Add(nextItem);
                }
            }
        }

        //================================================================================================//
        /// <summary>
        /// Add all switch and milepost info to TrackMonitorInfo
        /// </summary>
        /// 
        private void AddSwitch_MilepostInfo(ref TrainInfo thisInfo, int routeDirection)
        {
            // run along forward path to catch all diverging switches and mileposts
            var prevMilepostValue = -1f;
            var prevMilepostDistance = -1f;
            if (ValidRoute[routeDirection] != null)
            {
                TrainObjectItem thisItem;
                float distanceToTrainM = 0.0f;
                float offset = PresentPosition[routeDirection].TCOffset;
                TrackCircuitSection firstSection = signalRef.TrackCircuitList[PresentPosition[routeDirection].TCSectionIndex];
                float sectionStart = routeDirection == 0 ? -offset : offset - firstSection.Length;
                int startRouteIndex = PresentPosition[routeDirection].RouteListIndex;
                if (startRouteIndex < 0) startRouteIndex = ValidRoute[routeDirection].GetRouteIndex(PresentPosition[routeDirection].TCSectionIndex, 0);
                if (startRouteIndex >= 0)
                {
                    for (int iRouteElement = startRouteIndex; iRouteElement < ValidRoute[routeDirection].Count && distanceToTrainM < 7000 && sectionStart < 7000; iRouteElement++)
                    {
                        TrackCircuitSection thisSection = signalRef.TrackCircuitList[ValidRoute[routeDirection][iRouteElement].TCSectionIndex];
                        int sectionDirection = ValidRoute[routeDirection][iRouteElement].Direction;

                        if (thisSection.CircuitType == TrackCircuitSection.TrackCircuitType.Junction && (thisSection.Pins[sectionDirection, 1].Link != -1) && sectionStart < 7000)
                        {
                            bool isRightSwitch = true;
                            TrJunctionNode junctionNode = Simulator.TDB.TrackDB.TrackNodes[thisSection.OriginalIndex].TrJunctionNode;
                            var isDiverging = false;
                            if ((thisSection.ActivePins[sectionDirection, 1].Link > 0 && thisSection.JunctionDefaultRoute == 0) ||
                                (thisSection.ActivePins[sectionDirection, 0].Link > 0 && thisSection.JunctionDefaultRoute > 0))
                            {
                                // diverging 
                                isDiverging = true;
                                var junctionAngle = junctionNode.GetAngle(Simulator.TSectionDat);
                                if (junctionAngle < 0) isRightSwitch = false; 
                            }
                            if (isDiverging)
                            {
                                thisItem = new TrainObjectItem(isRightSwitch, sectionStart);
                                if (routeDirection == 0) thisInfo.ObjectInfoForward.Add(thisItem);
                                else thisInfo.ObjectInfoBackward.Add(thisItem);
                            }
                        }

                        if (thisSection.CircuitItems.TrackCircuitMileposts != null)
                        {
                            foreach (TrackCircuitMilepost thisMilepostItem in thisSection.CircuitItems.TrackCircuitMileposts)
                            {
                                Milepost thisMilepost = thisMilepostItem.MilepostRef;
                                distanceToTrainM = sectionStart + thisMilepostItem.MilepostLocation[sectionDirection == 1 ? 0 : 1];

                                if (!(distanceToTrainM - prevMilepostDistance < 50 && thisMilepost.MilepostValue == prevMilepostValue) && distanceToTrainM > 0 && distanceToTrainM < 7000)
                                {
                                    thisItem = new TrainObjectItem(thisMilepost.MilepostValue.ToString(), distanceToTrainM);
                                    prevMilepostDistance = distanceToTrainM;
                                    prevMilepostValue = thisMilepost.MilepostValue;
                                    if (routeDirection == 0) thisInfo.ObjectInfoForward.Add(thisItem);
                                    else thisInfo.ObjectInfoBackward.Add(thisItem);
                                }
                            }
                        }
                        sectionStart += thisSection.Length;
                    }
                }
            }
        }



        //================================================================================================//
        /// <summary>
        /// Add reversal info to TrackMonitorInfo
        /// </summary>

        public virtual void AddTrainReversalInfo(TCReversalInfo thisReversal, ref TrainInfo thisInfo)
        {
            if (!thisReversal.Valid && TCRoute.activeSubpath == TCRoute.TCRouteSubpaths.Count - 1) return;
            int reversalSection = thisReversal.ReversalSectionIndex;
            if (thisReversal.LastDivergeIndex >= 0)
            {
                reversalSection = thisReversal.SignalUsed ? thisReversal.SignalSectorIndex : thisReversal.DivergeSectorIndex;
            }

            TrackCircuitSection rearSection = signalRef.TrackCircuitList[PresentPosition[1].TCSectionIndex];
            float reversalDistanceM = rearSection.GetDistanceBetweenObjects(PresentPosition[1].TCSectionIndex, PresentPosition[1].TCOffset, PresentPosition[1].TCDirection,
            reversalSection, 0.0f);

            bool reversalEnabled = true;
            TrackCircuitSection frontSection = signalRef.TrackCircuitList[PresentPosition[0].TCSectionIndex];
            reversalDistanceM = Math.Max(reversalDistanceM, frontSection.GetDistanceBetweenObjects
                (PresentPosition[0].TCSectionIndex, PresentPosition[0].TCOffset, PresentPosition[0].TCDirection,
                thisReversal.ReversalSectionIndex, thisReversal.ReverseReversalOffset));
            int reversalIndex = thisReversal.SignalUsed ? thisReversal.LastSignalIndex : thisReversal.LastDivergeIndex;
            if (reversalDistanceM > 50f || (PresentPosition[1].RouteListIndex < reversalIndex))
            {
                reversalEnabled = false;
            }
            if (reversalDistanceM > 0)
            {
                TrainObjectItem nextItem = new TrainObjectItem(reversalEnabled, reversalDistanceM, thisReversal.Valid);
                thisInfo.ObjectInfoForward.Add(nextItem);
            }
        }

        //================================================================================================//
        /// <summary>
        /// Add waiting point info to TrackMonitorInfo
        /// </summary>

        public void AddWaitingPointInfo(ref TrainInfo thisInfo)
        {
            if (AuxActionsContain.SpecAuxActions.Count > 0 && AuxActionsContain.SpecAuxActions[0] is AIActionWPRef &&
                (AuxActionsContain.SpecAuxActions[0] as AIActionWPRef).SubrouteIndex == TCRoute.activeSubpath)
            {
                TrackCircuitSection frontSection = signalRef.TrackCircuitList[PresentPosition[0].TCSectionIndex];
                int thisSectionIndex = PresentPosition[0].TCSectionIndex;
                TrackCircuitSection thisSection = signalRef.TrackCircuitList[thisSectionIndex];
                float leftInSectionM = thisSection.Length - PresentPosition[0].TCOffset;

                // get action route index - if not found, return distances < 0

                int actionIndex0 = PresentPosition[0].RouteListIndex;
                int actionRouteIndex = ValidRoute[0].GetRouteIndex((AuxActionsContain.SpecAuxActions[0] as AIActionWPRef).TCSectionIndex, actionIndex0);
                var wpDistance = ValidRoute[0].GetDistanceAlongRoute(actionIndex0, leftInSectionM, actionRouteIndex, (AuxActionsContain.SpecAuxActions[0] as AIActionWPRef).RequiredDistance, AITrainDirectionForward, signalRef);
                bool wpEnabled = false;
                if (SpeedMpS == 0 && (((AuxActionsContain.SpecAuxActions[0] as AIActionWPRef).keepIt != null &&
                    (AuxActionsContain.SpecAuxActions[0] as AIActionWPRef).keepIt.currentMvmtState == AITrain.AI_MOVEMENT_STATE.HANDLE_ACTION) ||
                    ((this as AITrain).nextActionInfo is AuxActionWPItem && (this as AITrain).MovementState == AITrain.AI_MOVEMENT_STATE.HANDLE_ACTION))) wpEnabled = true;

                TrainObjectItem nextItem = new TrainObjectItem(wpDistance, wpEnabled);
                thisInfo.ObjectInfoForward.Add(nextItem);
            }
        }

        //================================================================================================//
        /// <summary>
        /// Create TrackInfoObject for information in TrackMonitor window when in Manual mode
        /// </summary>

        public void GetTrainInfoManual(ref TrainInfo thisInfo)
        {
            // set control mode
            thisInfo.ControlMode = ControlMode;

            // set speed
            thisInfo.speedMpS = SpeedMpS;

            // set projected speed
            thisInfo.projectedSpeedMpS = ProjectedSpeedMpS;

            // set max speed
            thisInfo.allowedSpeedMpS = Math.Min(AllowedMaxSpeedMpS, TrainMaxSpeedMpS);

            // set gradient
            thisInfo.currentElevationPercent = Simulator.PlayerLocomotive != null ? Simulator.PlayerLocomotive.CurrentElevationPercent : 0;

            // set direction
            thisInfo.direction = MUDirection == Direction.Forward ? 0 : (MUDirection == Direction.Reverse ? 1 : -1);

            // set orientation
            thisInfo.cabOrientation = (Simulator.PlayerLocomotive.Flipped ^ Simulator.PlayerLocomotive.GetCabFlipped()) ? 1 : 0;

            // check if train is on original path
            thisInfo.isOnPath = false;
            if (TCRoute != null && TCRoute.activeSubpath >= 0 && TCRoute.TCRouteSubpaths != null && TCRoute.TCRouteSubpaths.Count > TCRoute.activeSubpath)
            {
                TCSubpathRoute validPath = TCRoute.TCRouteSubpaths[TCRoute.activeSubpath];
                int routeIndex = validPath.GetRouteIndex(PresentPosition[0].TCSectionIndex, 0);
                thisInfo.isOnPath = (routeIndex >= 0);
            }

            // set forward information

            // set authority
            TrainObjectItem thisItem = new TrainObjectItem(EndAuthorityType[0], DistanceToEndNodeAuthorityM[0]);
            thisInfo.ObjectInfoForward.Add(thisItem);

            // run along forward path to catch all speedposts and signals

            if (ValidRoute[0] != null)
            {
                float distanceToTrainM = 0.0f;
                float offset = PresentPosition[0].TCOffset;
                float sectionStart = -offset;
                float progressiveMaxSpeedLimitMpS = allowedMaxSpeedLimitMpS;

                foreach (TCRouteElement thisElement in ValidRoute[0])
                {
                    TrackCircuitSection thisSection = signalRef.TrackCircuitList[thisElement.TCSectionIndex];
                    int sectionDirection = thisElement.Direction;

                    if (thisSection.EndSignals[sectionDirection] != null)
                    {
                        distanceToTrainM = sectionStart + thisSection.Length;
                        var thisSignal = thisSection.EndSignals[sectionDirection];
                        var thisSpeedInfo = thisSignal.this_sig_speed(MstsSignalFunction.NORMAL);
                        float validSpeed = thisSpeedInfo == null ? -1 : (IsFreight ? thisSpeedInfo.speed_freight : thisSpeedInfo.speed_pass);

                        TrackMonitorSignalAspect signalAspect = thisSignal.TranslateTMAspect(thisSignal.this_sig_lr(MstsSignalFunction.NORMAL));
                        thisItem = new TrainObjectItem(signalAspect, validSpeed, distanceToTrainM);
                        thisInfo.ObjectInfoForward.Add(thisItem);
                    }

                    if (thisSection.CircuitItems.TrackCircuitSpeedPosts[sectionDirection] != null)
                    {
                        foreach (TrackCircuitSignalItem thisSpeeditem in thisSection.CircuitItems.TrackCircuitSpeedPosts[sectionDirection].TrackCircuitItem)
                        {
                            var thisSpeedpost = thisSpeeditem.SignalRef;
                            var thisSpeedInfo = thisSpeedpost.this_sig_speed(MstsSignalFunction.SPEED);
                            float validSpeed = thisSpeedInfo == null ? -1 : (IsFreight ? thisSpeedInfo.speed_freight : thisSpeedInfo.speed_pass);

                            distanceToTrainM = sectionStart + thisSpeeditem.SignalLocation;

                            if (distanceToTrainM > 0 && (validSpeed > 0 || (thisSpeedInfo != null && thisSpeedInfo.speed_reset == 1)))
                            {
                                if (thisSpeedInfo != null && thisSpeedInfo.speed_reset == 1)
                                    validSpeed = progressiveMaxSpeedLimitMpS;
                                else progressiveMaxSpeedLimitMpS = validSpeed;
                                thisItem = new TrainObjectItem(validSpeed, distanceToTrainM, (TrainObjectItem.SpeedItemType)thisSpeedpost.SpeedPostType());
                                thisInfo.ObjectInfoForward.Add(thisItem);
                            }
                        }
                    }

                    sectionStart += thisSection.Length;
                }
            }

                // do it separately for switches and mileposts
            // run along forward path to catch all diverging switches and mileposts

            AddSwitch_MilepostInfo(ref thisInfo, 0);
 
            // set backward information

            // set authority
            thisItem = new TrainObjectItem(EndAuthorityType[1], DistanceToEndNodeAuthorityM[1]);
            thisInfo.ObjectInfoBackward.Add(thisItem);

            // run along backward path to catch all speedposts and signals

            if (ValidRoute[1] != null)
            {
                float distanceToTrainM = 0.0f;
                float offset = PresentPosition[1].TCOffset;
                TrackCircuitSection firstSection = signalRef.TrackCircuitList[PresentPosition[1].TCSectionIndex];
                float sectionStart = offset - firstSection.Length;
                float progressiveMaxSpeedLimitMpS = allowedMaxSpeedLimitMpS;

                foreach (TCRouteElement thisElement in ValidRoute[1])
                {
                    TrackCircuitSection thisSection = signalRef.TrackCircuitList[thisElement.TCSectionIndex];
                    int sectionDirection = thisElement.Direction;

                    if (thisSection.EndSignals[sectionDirection] != null)
                    {
                        distanceToTrainM = sectionStart + thisSection.Length;
                        SignalObject thisSignal = thisSection.EndSignals[sectionDirection];
                        ObjectSpeedInfo thisSpeedInfo = thisSignal.this_sig_speed(MstsSignalFunction.NORMAL);
                        float validSpeed = thisSpeedInfo == null ? -1 : (IsFreight ? thisSpeedInfo.speed_freight : thisSpeedInfo.speed_pass);

                        TrackMonitorSignalAspect signalAspect = thisSignal.TranslateTMAspect(thisSignal.this_sig_lr(MstsSignalFunction.NORMAL));
                        thisItem = new TrainObjectItem(signalAspect, validSpeed, distanceToTrainM);
                        thisInfo.ObjectInfoBackward.Add(thisItem);
                    }

                    if (thisSection.CircuitItems.TrackCircuitSpeedPosts[sectionDirection] != null)
                    {
                        foreach (TrackCircuitSignalItem thisSpeeditem in thisSection.CircuitItems.TrackCircuitSpeedPosts[sectionDirection].TrackCircuitItem)
                        {
                            SignalObject thisSpeedpost = thisSpeeditem.SignalRef;
                            ObjectSpeedInfo thisSpeedInfo = thisSpeedpost.this_sig_speed(MstsSignalFunction.SPEED);
                            float validSpeed = thisSpeedInfo == null ? -1 : (IsFreight ? thisSpeedInfo.speed_freight : thisSpeedInfo.speed_pass);
                            distanceToTrainM = sectionStart + thisSpeeditem.SignalLocation;

                            if (distanceToTrainM > 0 && (validSpeed > 0 || (thisSpeedInfo != null && thisSpeedInfo.speed_reset == 1)))
                            {
                                if (thisSpeedInfo != null && thisSpeedInfo.speed_reset == 1)
                                    validSpeed = progressiveMaxSpeedLimitMpS;
                                else progressiveMaxSpeedLimitMpS = validSpeed;
                                thisItem = new TrainObjectItem(validSpeed, distanceToTrainM, (TrainObjectItem.SpeedItemType)thisSpeedpost.SpeedPostType());
                                thisInfo.ObjectInfoBackward.Add(thisItem);
                            }
                        }
                    }

                    sectionStart += thisSection.Length;
                }
            }
            
                // do it separately for switches and mileposts
            AddSwitch_MilepostInfo(ref thisInfo, 1);
        }

        //================================================================================================//
        /// <summary>
        /// Create TrackInfoObject for information in TrackMonitor window when OutOfControl
        /// </summary>

        public void GetTrainInfoOOC(ref TrainInfo thisInfo)
        {
            // set control mode
            thisInfo.ControlMode = ControlMode;

            // set speed
            thisInfo.speedMpS = SpeedMpS;

            // set projected speed
            thisInfo.projectedSpeedMpS = ProjectedSpeedMpS;

            // set max speed
            thisInfo.allowedSpeedMpS = Math.Min(AllowedMaxSpeedMpS, TrainMaxSpeedMpS);

            // set direction
            thisInfo.direction = MUDirection == Direction.Forward ? 0 : 1;

            // set orientation
            thisInfo.cabOrientation = (Simulator.PlayerLocomotive.Flipped ^ Simulator.PlayerLocomotive.GetCabFlipped()) ? 1 : 0;

            // set out of control reason
            TrainObjectItem thisItem = new TrainObjectItem(OutOfControlReason);
            thisInfo.ObjectInfoForward.Add(thisItem);
        }

        //================================================================================================//
        /// <summary>
        /// Create Track Circuit Route Path
        /// </summary>

        public void SetRoutePath(AIPath aiPath)
        {
#if DEBUG_TEST
            File.AppendAllText(@"C:\temp\TCSections.txt", "--------------------------------------------------\n");
            File.AppendAllText(@"C:\temp\TCSections.txt", "Train : " + Number.ToString() + "\n\n");
#endif
            TCRoute = new TCRoutePath(aiPath, (int)FrontTDBTraveller.Direction, Length, signalRef, Number, Simulator.Settings);
            ValidRoute[0] = TCRoute.TCRouteSubpaths[TCRoute.activeSubpath];
        }


        //================================================================================================//
        /// <summary>
        /// Create Track Circuit Route Path
        /// </summary>

        public void SetRoutePath(AIPath aiPath, Signals orgSignals)
        {
#if DEBUG_TEST
            File.AppendAllText(@"C:\temp\TCSections.txt", "--------------------------------------------------\n");
            File.AppendAllText(@"C:\temp\TCSections.txt", "Train : " + Number.ToString() + "\n\n");
#endif
            int orgDirection = (RearTDBTraveller != null) ? (int)RearTDBTraveller.Direction : -2;
            TCRoute = new TCRoutePath(aiPath, orgDirection, Length, orgSignals, Number, Simulator.Settings);
            ValidRoute[0] = TCRoute.TCRouteSubpaths[TCRoute.activeSubpath];
        }

        //================================================================================================//
        //
        // Preset switches for explorer mode
        //

        public void PresetExplorerPath(AIPath aiPath, Signals orgSignals)
        {
            int orgDirection = (RearTDBTraveller != null) ? (int)RearTDBTraveller.Direction : -2;
            TCRoute = new TCRoutePath(aiPath, orgDirection, 0, orgSignals, Number, Simulator.Settings);

            // loop through all sections in first subroute except first and last (neither can be junction)

            for (int iElement = 1; iElement <= TCRoute.TCRouteSubpaths[0].Count - 2; iElement++)
            {
                TrackCircuitSection thisSection = orgSignals.TrackCircuitList[TCRoute.TCRouteSubpaths[0][iElement].TCSectionIndex];
                int nextSectionIndex = TCRoute.TCRouteSubpaths[0][iElement + 1].TCSectionIndex;
                int prevSectionIndex = TCRoute.TCRouteSubpaths[0][iElement - 1].TCSectionIndex;

                // process Junction

                if (thisSection.CircuitType == TrackCircuitSection.TrackCircuitType.Junction)
                {
                    if (thisSection.Pins[0, 0].Link == nextSectionIndex && !MPManager.NoAutoSwitch())
                    {
                        thisSection.alignSwitchPins(prevSectionIndex);   // trailing switch
                    }
                    else
                    {
                        thisSection.alignSwitchPins(nextSectionIndex);   // facing switch
                    }
                }
            }
        }

        //================================================================================================//

        /// <summary>
        /// Get total length of reserved section ahead of train
        /// </summary>
        /// <returns></returns>
        private float GetReservedLength()
        {
            float totalLength = 0f;
            TCSubpathRoute usedRoute = null;
            int routeListIndex = -1;
            float presentOffset = 0f;
            TrainRouted routedTrain = null;

            if (MUDirection == Direction.Forward || MUDirection == Direction.N || ValidRoute[1] == null)
            {
                usedRoute = ValidRoute[0];
                routeListIndex = PresentPosition[0].RouteListIndex;
                presentOffset = PresentPosition[0].TCOffset;
                routedTrain = routedForward;
            }
            else
            {
                usedRoute = ValidRoute[1];
                routeListIndex = PresentPosition[1].RouteListIndex;
                presentOffset = PresentPosition[1].TCOffset;
                routedTrain = routedBackward;
            }

            if (routeListIndex >= 0 && usedRoute != null && routeListIndex <= (usedRoute.Count - 1))
            {
                TrackCircuitSection thisSection = signalRef.TrackCircuitList[usedRoute[routeListIndex].TCSectionIndex];
                totalLength = thisSection.Length - presentOffset;

                while (routeListIndex < usedRoute.Count - 1)
                {
                    routeListIndex++;
                    thisSection = signalRef.TrackCircuitList[usedRoute[routeListIndex].TCSectionIndex];
                    if (thisSection.IsSet(routedTrain, false))
                    {
                        totalLength += thisSection.Length;
                    }
                    else
                    {
                        break;
                    }
                }
            }

            return (totalLength);
        }

        //================================================================================================//
        //
        // Extract alternative route
        //

        public TCSubpathRoute ExtractAlternativeRoute_pathBased(int altRouteIndex)
        {
            TCSubpathRoute returnRoute = new TCSubpathRoute();

            // extract entries of alternative route upto first signal

            foreach (TCRouteElement thisElement in TCRoute.TCAlternativePaths[altRouteIndex])
            {
                returnRoute.Add(thisElement);
                TrackCircuitSection thisSection = signalRef.TrackCircuitList[thisElement.TCSectionIndex];
                if (thisSection.EndSignals[thisElement.Direction] != null)
                {
                    break;
                }
            }

            return (returnRoute);
        }

        //================================================================================================//
        //
        // Extract alternative route
        //

        public TCSubpathRoute ExtractAlternativeRoute_locationBased(TCSubpathRoute altRoute)
        {
            TCSubpathRoute returnRoute = new TCSubpathRoute();

            // extract entries of alternative route upto first signal

            foreach (TCRouteElement thisElement in altRoute)
            {
                returnRoute.Add(thisElement);
                TrackCircuitSection thisSection = signalRef.TrackCircuitList[thisElement.TCSectionIndex];
                if (thisSection.EndSignals[thisElement.Direction] != null)
                {
                    break;
                }
            }

            return (returnRoute);
        }

        //================================================================================================//
        //
        // Set train route to alternative route - path based deadlock processing
        //

        public virtual void SetAlternativeRoute_pathBased(int startElementIndex, int altRouteIndex, SignalObject nextSignal)
        {

#if DEBUG_REPORTS
            File.AppendAllText(@"C:\temp\printproc.txt", "Train " + Number.ToString() +
                    " : set alternative route no. : " + altRouteIndex.ToString() +
                    " from section " + ValidRoute[0][startElementIndex].TCSectionIndex.ToString() +
                    " (request from signal " + nextSignal.thisRef.ToString() + " )\n");
#endif

            if (CheckTrain)
            {
                File.AppendAllText(@"C:\temp\checktrain.txt", "Train " + Number.ToString() +
                " : set alternative route no. : " + altRouteIndex.ToString() +
                " from section " + ValidRoute[0][startElementIndex].TCSectionIndex.ToString() +
                " (request from signal " + nextSignal.thisRef.ToString() + " )\n");
            }

            // set new train route

            TCSubpathRoute thisRoute = ValidRoute[0];
            TCSubpathRoute newRoute = new TCSubpathRoute();
            int actSubpath = TCRoute.activeSubpath;

            TCSubpathRoute altRoute = TCRoute.TCAlternativePaths[altRouteIndex];
            TCRoute.activeAltpath = altRouteIndex;

            // part upto split

            for (int iElement = 0; iElement < startElementIndex; iElement++)
            {
                newRoute.Add(thisRoute[iElement]);
            }

            // alternative path

            for (int iElement = 0; iElement < altRoute.Count; iElement++)
            {
                newRoute.Add(altRoute[iElement]);
            }
            int lastAlternativeSectionIndex = thisRoute.GetRouteIndex(altRoute[altRoute.Count - 1].TCSectionIndex, startElementIndex);

            // check for any stations in abandoned path
            Dictionary<int, StationStop> abdStations = new Dictionary<int, StationStop>();
            CheckAbandonedStations(startElementIndex, lastAlternativeSectionIndex, actSubpath, abdStations);

            // continued path

            for (int iElement = lastAlternativeSectionIndex + 1; iElement < thisRoute.Count; iElement++)
            {
                newRoute.Add(thisRoute[iElement]);
            }
            // Reindexes ReversalInfo items
            var countDifference = newRoute.Count - ValidRoute[0].Count;
            if (TCRoute.ReversalInfo[TCRoute.activeSubpath].LastDivergeIndex >= 0)
                TCRoute.ReversalInfo[TCRoute.activeSubpath].LastDivergeIndex = TCRoute.ReversalInfo[TCRoute.activeSubpath].LastDivergeIndex + countDifference;
            if (TCRoute.ReversalInfo[TCRoute.activeSubpath].LastSignalIndex >= 0)
                TCRoute.ReversalInfo[TCRoute.activeSubpath].LastSignalIndex = TCRoute.ReversalInfo[TCRoute.activeSubpath].LastSignalIndex + countDifference;

            // set new route

            ValidRoute[0] = newRoute;
            TCRoute.TCRouteSubpaths[TCRoute.activeSubpath] = newRoute;

            // check for abandoned stations - try to find alternative on passing path
            LookForReplacementStations(abdStations, newRoute, altRoute);
 
            // set signal route
            // part upto split

            TCSubpathRoute newSignalRoute = new TCSubpathRoute();

            int splitSignalIndex = nextSignal.signalRoute.GetRouteIndex(thisRoute[startElementIndex].TCSectionIndex, 0);
            for (int iElement = 0; iElement < splitSignalIndex; iElement++)
            {
                newSignalRoute.Add(nextSignal.signalRoute[iElement]);
            }

            // extract new route upto next signal

            TCSubpathRoute nextPart = ExtractAlternativeRoute_pathBased(altRouteIndex);
            foreach (TCRouteElement thisElement in nextPart)
            {
                newSignalRoute.Add(thisElement);
            }

            nextSignal.ResetSignal(true);
            nextSignal.signalRoute = newSignalRoute;

            if (ControlMode == TRAIN_CONTROL.AUTO_SIGNAL)
            {
                // keep any items allready passed
                List<ObjectItemInfo> keeplist = new List<ObjectItemInfo>();
                foreach (ObjectItemInfo checkItem in SignalObjectItems)
                {
                    float actualDistance = GetObjectDistanceToTrain(checkItem);
                    if (actualDistance < 0)
                    {
                        keeplist.Add(checkItem);
                    }
                }

                // create new list
                InitializeSignals(true);

                // add any passed items (in reverse order at start of list)
                if (keeplist.Count > 0)
                {
                    for (int iObject = keeplist.Count - 1; iObject >= 0; iObject--)
                    {
                        SignalObjectItems.Insert(0, keeplist[iObject]);
                    }
                }

                // find new next signal
                NextSignalObject[0] = null;
                for (int iObject = 0; iObject <= SignalObjectItems.Count - 1 && NextSignalObject[0] == null; iObject++)
                {
                    if (SignalObjectItems[iObject].ObjectType == ObjectItemInfo.ObjectItemType.Signal)
                    {
                        NextSignalObject[0] = SignalObjectItems[iObject].ObjectDetails;
                    }
                }

                if (NextSignalObject[0] != null)
                {
                    NextSignalObject[0].requestClearSignal(ValidRoute[0], routedForward, 0, false, null);
                }
            }
        }

        //================================================================================================//
        //
        // Set train route to alternative route - location based deadlock processing
        //

        public virtual void SetAlternativeRoute_locationBased(int startSectionIndex, DeadlockInfo sectionDeadlockInfo, int usedPath, SignalObject nextSignal)
        {
#if DEBUG_REPORTS
            File.AppendAllText(@"C:\temp\printproc.txt", "Train " + Number.ToString() +
            " : set alternative route no. : " + usedPath.ToString() +
            " from section " + startSectionIndex.ToString() +
            " (request from signal " + nextSignal.thisRef.ToString() + " )\n");
#endif

            if (CheckTrain)
            {
                File.AppendAllText(@"C:\temp\checktrain.txt", "Train " + Number.ToString() +
                " : set alternative route no. : " + usedPath.ToString() +
                " from section " + startSectionIndex.ToString() +
                " (request from signal " + nextSignal.thisRef.ToString() + " )\n");
            }

            // set new train route

            TCSubpathRoute thisRoute = ValidRoute[0];
            TCSubpathRoute newRoute = new TCSubpathRoute();

            TCSubpathRoute altRoute = sectionDeadlockInfo.AvailablePathList[usedPath].Path;
            int actSubpath = TCRoute.activeSubpath;

            // part upto split

            int startElementIndex = thisRoute.GetRouteIndex(startSectionIndex, PresentPosition[0].RouteListIndex);
            for (int iElement = 0; iElement < startElementIndex; iElement++)
            {
                newRoute.Add(thisRoute[iElement]);
            }

            // alternative path

            for (int iElement = 0; iElement < altRoute.Count; iElement++)
            {
                newRoute.Add(altRoute[iElement]);
            }

            // check for any deadlocks on abandoned path - but only if not on new path

            int lastAlternativeSectionIndex = thisRoute.GetRouteIndex(altRoute[altRoute.Count - 1].TCSectionIndex, startElementIndex);
            for (int iElement = startElementIndex; iElement <= lastAlternativeSectionIndex; iElement++)
            {
                TrackCircuitSection abdSection = signalRef.TrackCircuitList[thisRoute[iElement].TCSectionIndex];

#if DEBUG_DEADLOCK
                File.AppendAllText(@"C:\temp\deadlock.txt","Abandoning section " + abdSection.Index + " for Train " + Number + "\n");
#endif

                if (newRoute.GetRouteIndex(abdSection.Index, 0) < 0)
                {

#if DEBUG_DEADLOCK
                File.AppendAllText(@"C:\temp\deadlock.txt","Removing deadlocks for section " + abdSection.Index + " for Train " + Number + "\n");
#endif

                    abdSection.ClearDeadlockTrap(Number);
                }

#if DEBUG_DEADLOCK
                else
                {
                    File.AppendAllText(@"C:\temp\deadlock.txt","Section " + abdSection.Index + " for Train " + Number + " in new route, not removing deadlocks\n");
                }
                File.AppendAllText(@"C:\temp\deadlock.txt", "\n");
#endif

            }

#if DEBUG_DEADLOCK
            File.AppendAllText(@"C:\temp\deadlock.txt", "\n");
#endif
            // check for any stations in abandoned path

            Dictionary<int, StationStop> abdStations = new Dictionary<int, StationStop>();
            CheckAbandonedStations(startElementIndex, lastAlternativeSectionIndex, actSubpath, abdStations);

            // continued path

            for (int iElement = lastAlternativeSectionIndex + 1; iElement < thisRoute.Count; iElement++)
            {
                newRoute.Add(thisRoute[iElement]);
            }

            // Reindexes ReversalInfo items
            var countDifference = newRoute.Count - ValidRoute[0].Count;
            if (TCRoute.ReversalInfo[TCRoute.activeSubpath].LastDivergeIndex >= 0)
                TCRoute.ReversalInfo[TCRoute.activeSubpath].LastDivergeIndex = TCRoute.ReversalInfo[TCRoute.activeSubpath].LastDivergeIndex + countDifference;
            if (TCRoute.ReversalInfo[TCRoute.activeSubpath].LastSignalIndex >= 0)
                TCRoute.ReversalInfo[TCRoute.activeSubpath].LastSignalIndex = TCRoute.ReversalInfo[TCRoute.activeSubpath].LastSignalIndex + countDifference;

            // set new route

            ValidRoute[0] = newRoute;
            TCRoute.TCRouteSubpaths[TCRoute.activeSubpath] = newRoute;

            // check for abandoned stations - try to find alternative on passing path
            LookForReplacementStations(abdStations, newRoute, altRoute);
 
            // set signal route
            // part upto split

            if (nextSignal != null)
            {
                TCSubpathRoute newSignalRoute = new TCSubpathRoute();

                int splitSignalIndex = nextSignal.signalRoute.GetRouteIndex(thisRoute[startElementIndex].TCSectionIndex, 0);
                for (int iElement = 0; iElement < splitSignalIndex; iElement++)
                {
                    newSignalRoute.Add(nextSignal.signalRoute[iElement]);
                }

                // extract new route upto next signal

                TCSubpathRoute nextPart = ExtractAlternativeRoute_locationBased(altRoute);
                foreach (TCRouteElement thisElement in nextPart)
                {
                    newSignalRoute.Add(thisElement);
                }

                // set new signal route
                // reset signal
                // if train in signal mode, request clear signal

                nextSignal.ResetSignal(true);
                nextSignal.signalRoute = newSignalRoute;

                if (ControlMode == TRAIN_CONTROL.AUTO_SIGNAL)
                {
                    // keep any items allready passed
                    List<ObjectItemInfo> keeplist = new List<ObjectItemInfo>();
                    foreach (ObjectItemInfo checkItem in SignalObjectItems)
                    {
                        float actualDistance = GetObjectDistanceToTrain(checkItem);
                        if (actualDistance < 0)
                        {
                            keeplist.Add(checkItem);
                        }
                    }

                    // create new list
                    InitializeSignals(true);

                    // add any passed items (in reverse order at start of list)
                    if (keeplist.Count > 0)
                    {
                        for (int iObject = keeplist.Count - 1; iObject >= 0; iObject--)
                        {
                            SignalObjectItems.Insert(0, keeplist[iObject]);
                        }
                    }

                    // find new next signal
                    NextSignalObject[0] = null;
                    for (int iObject = 0; iObject <= SignalObjectItems.Count - 1 && NextSignalObject[0] == null; iObject++)
                    {
                        if (SignalObjectItems[iObject].ObjectType == ObjectItemInfo.ObjectItemType.Signal)
                        {
                            NextSignalObject[0] = SignalObjectItems[iObject].ObjectDetails;
                            DistanceToSignal = SignalObjectItems[iObject].distance_to_train;
                        }
                    }

                    if (NextSignalObject[0] != null)
                    {
                        NextSignalObject[0].requestClearSignal(ValidRoute[0], routedForward, 0, false, null);
                    }
                }
            }
        }

        //================================================================================================//
        //
        // Check for abandoned stations in the abandoned path
        //
        //
        private void CheckAbandonedStations(int startElementIndex, int lastAlternativeSectionIndex, int actSubpath, Dictionary<int, StationStop> abdStations)
        {
            int nextStationIndex = 0;


            if (StationStops != null && StationStops.Count > 0)
            {
                int stationRouteIndex = StationStops[nextStationIndex].RouteIndex;
                int stationSubpath = StationStops[nextStationIndex].SubrouteIndex;

                while (stationRouteIndex < lastAlternativeSectionIndex)
                {
                    if (stationSubpath == actSubpath && stationRouteIndex > startElementIndex)
                    {
                        abdStations.Add(nextStationIndex, StationStops[nextStationIndex]);
                    }

                    nextStationIndex++;
                    if (nextStationIndex > StationStops.Count - 1)
                    {
                        stationRouteIndex = lastAlternativeSectionIndex + 1;  // no more stations - set index beyond end
                    }
                    else
                    {
                        stationRouteIndex = StationStops[nextStationIndex].RouteIndex;
                        stationSubpath = StationStops[nextStationIndex].SubrouteIndex;
                        if (stationSubpath > actSubpath)
                        {
                            stationRouteIndex = lastAlternativeSectionIndex + 1; // no more stations in this subpath
                        }
                    }
                }
            }
        }

        //================================================================================================//
        //
        // Look for stations in alternative route
        //
        //
        private void LookForReplacementStations(Dictionary<int, StationStop> abdStations, TCSubpathRoute newRoute, TCSubpathRoute altRoute)
        {

            if (StationStops != null)
            {
                List<StationStop> newStops = new List<StationStop>();
                int firstIndex = -1;

                foreach (KeyValuePair<int, StationStop> abdStop in abdStations)
                {
                    if (firstIndex < 0) firstIndex = abdStop.Key;
                    StationStop newStop = SetAlternativeStationStop(abdStop.Value, altRoute);
                    StationStops.RemoveAt(firstIndex);
                    if (newStop != null)
                    {
                        newStops.Add(newStop);
                    }
                }

                for (int iStop = newStops.Count - 1; iStop >= 0; iStop--)
                {
                    StationStops.Insert(firstIndex, newStops[iStop]);
                }

                // recalculate indices of all stops
                int prevIndex = 0;
                foreach (StationStop statStop in StationStops)
                {
                    statStop.RouteIndex = newRoute.GetRouteIndex(statStop.TCSectionIndex, prevIndex);
                    prevIndex = statStop.RouteIndex;
                }
            }
        }

        //================================================================================================//
        //
        // Find station on alternative route
        //
        //

        public virtual StationStop SetAlternativeStationStop(StationStop orgStop, TCSubpathRoute newRoute)
        {
            int altPlatformIndex = -1;

            // get station platform list
            if (signalRef.StationXRefList.ContainsKey(orgStop.PlatformItem.Name))
            {
                List<int> XRefKeys = signalRef.StationXRefList[orgStop.PlatformItem.Name];

                // search through all available platforms
                for (int platformIndex = 0; platformIndex <= XRefKeys.Count - 1 && altPlatformIndex < 0; platformIndex++)
                {
                    int platformXRefIndex = XRefKeys[platformIndex];
                    PlatformDetails altPlatform = signalRef.PlatformDetailsList[platformXRefIndex];

                    // check if section is in new route
                    for (int iSectionIndex = 0; iSectionIndex <= altPlatform.TCSectionIndex.Count - 1 && altPlatformIndex < 0; iSectionIndex++)
                    {
                        if (newRoute.GetRouteIndex(altPlatform.TCSectionIndex[iSectionIndex], 0) > 0)
                        {
                            altPlatformIndex = platformXRefIndex;
                        }
                    }
                }

                // section found in new route - set new station details using old details
                if (altPlatformIndex > 0)
                {
                    StationStop newStop = CalculateStationStop(signalRef.PlatformDetailsList[altPlatformIndex].PlatformReference[0],
                        orgStop.ArrivalTime, orgStop.DepartTime, orgStop.arrivalDT, orgStop.departureDT, 15.0f);

#if DEBUG_REPORTS
                    if (newStop != null)
                    {
                        File.AppendAllText(@"C:\temp\printproc.txt", "Train " + Number.ToString() +
                        " : alternative stop required for " + orgStop.PlatformItem.Name +
                        " ; found : " + newStop.PlatformReference + "\n");
                    }
                    else
                    {
                        File.AppendAllText(@"C:\temp\printproc.txt", "Train " + Number.ToString() +
                        " : alternative stop required for " + orgStop.PlatformItem.Name +
                        " ; not found \n");
                    }
#endif

                    if (CheckTrain)
                    {
                        if (newStop != null)
                        {
                            File.AppendAllText(@"C:\temp\checktrain.txt", "Train " + Number.ToString() +
                            " : alternative stop required for " + orgStop.PlatformItem.Name +
                            " ; found : " + newStop.PlatformReference + "\n");
                        }
                        else
                        {
                            File.AppendAllText(@"C:\temp\checktrain.txt", "Train " + Number.ToString() +
                            " : alternative stop required for " + orgStop.PlatformItem.Name +
                            " ; not found \n");
                        }
                    }

                    return (newStop);
                }
            }

            return (null);
        }

        //================================================================================================//
        /// <summary>
        /// Create station stop (used in activity mode only)
        /// <\summary>

        public StationStop CalculateStationStop(int platformStartID, int arrivalTime, int departTime, DateTime arrivalDT, DateTime departureDT, float clearingDistanceM)
        {
            int platformIndex;
            int lastRouteIndex = 0;
            int activeSubroute = 0;

            TCSubpathRoute thisRoute = TCRoute.TCRouteSubpaths[activeSubroute];

            // get platform details

            if (!signalRef.PlatformXRefList.TryGetValue(platformStartID, out platformIndex))
            {
                return (null); // station not found
            }
            else
            {
                PlatformDetails thisPlatform = signalRef.PlatformDetailsList[platformIndex];
                int sectionIndex = thisPlatform.TCSectionIndex[0];
                int routeIndex = thisRoute.GetRouteIndex(sectionIndex, 0);

                // if first section not found in route, try last

                if (routeIndex < 0)
                {
                    sectionIndex = thisPlatform.TCSectionIndex[thisPlatform.TCSectionIndex.Count - 1];
                    routeIndex = thisRoute.GetRouteIndex(sectionIndex, 0);
                }

                // if neither section found - try next subroute - keep trying till found or out of subroutes

                while (routeIndex < 0 && activeSubroute < (TCRoute.TCRouteSubpaths.Count - 1))
                {
                    activeSubroute++;
                    thisRoute = TCRoute.TCRouteSubpaths[activeSubroute];
                    routeIndex = thisRoute.GetRouteIndex(sectionIndex, 0);

                    // if first section not found in route, try last

                    if (routeIndex < 0)
                    {
                        sectionIndex = thisPlatform.TCSectionIndex[thisPlatform.TCSectionIndex.Count - 1];
                        routeIndex = thisRoute.GetRouteIndex(sectionIndex, 0);
                    }
                }

                // if neither section found - platform is not on route - skip

                if (routeIndex < 0)
                {
                    Trace.TraceWarning("Train {0} Service {1} : platform {2} is not on route",
                            Number.ToString(), Name, platformStartID.ToString());
                    return (null);
                }

                // determine end stop position depending on direction

                TCRouteElement thisElement = thisRoute[routeIndex];

                int endSectionIndex = thisElement.Direction == 0 ?
                    thisPlatform.TCSectionIndex[thisPlatform.TCSectionIndex.Count - 1] :
                    thisPlatform.TCSectionIndex[0];
                int beginSectionIndex = thisElement.Direction == 0 ?
                    thisPlatform.TCSectionIndex[0] :
                    thisPlatform.TCSectionIndex[thisPlatform.TCSectionIndex.Count - 1];

                float endOffset = thisPlatform.TCOffset[1, thisElement.Direction];
                float beginOffset = thisPlatform.TCOffset[0, thisElement.Direction];

                float deltaLength = thisPlatform.Length - Length; // platform length - train length

                TrackCircuitSection endSection = signalRef.TrackCircuitList[endSectionIndex];


                int firstRouteIndex = thisRoute.GetRouteIndex(beginSectionIndex, 0);
                if (firstRouteIndex < 0)
                    firstRouteIndex = routeIndex;
                lastRouteIndex = thisRoute.GetRouteIndex(endSectionIndex, 0);
                if (lastRouteIndex < 0)
                    lastRouteIndex = routeIndex;

                float stopOffset = 0;
                float fullLength = thisPlatform.Length;


                // if train too long : search back for platform with same name
                if (deltaLength < 0)
                {
                    float actualBegin = beginOffset;

                    TrackCircuitSection thisSection = signalRef.TrackCircuitList[beginSectionIndex];

                    // Other platforms in same section

                    if (thisSection.PlatformIndex.Count > 1)
                    {
                        foreach (int nextIndex in thisSection.PlatformIndex)
                        {
                            if (nextIndex != platformIndex)
                            {
                                PlatformDetails otherPlatform = signalRef.PlatformDetailsList[nextIndex];
                                if (String.Compare(otherPlatform.Name, thisPlatform.Name) == 0)
                                {
                                    int otherSectionIndex = thisElement.Direction == 0 ?
                                        otherPlatform.TCSectionIndex[0] :
                                        otherPlatform.TCSectionIndex[thisPlatform.TCSectionIndex.Count - 1];
                                    if (otherSectionIndex == beginSectionIndex)
                                    {
                                        if (otherPlatform.TCOffset[0, thisElement.Direction] < actualBegin)
                                        {
                                            actualBegin = otherPlatform.TCOffset[0, thisElement.Direction];
                                            fullLength = endOffset - actualBegin;
                                        }
                                    }
                                    else
                                    {
                                        int addRouteIndex = thisRoute.GetRouteIndex(otherSectionIndex, 0);
                                        float addOffset = otherPlatform.TCOffset[1, thisElement.Direction == 0 ? 1 : 0];
                                        // offset of begin in other direction is length of available track

                                        if (lastRouteIndex > 0)
                                        {
                                            float thisLength =
                                                thisRoute.GetDistanceAlongRoute(addRouteIndex, addOffset,
                                                        lastRouteIndex, endOffset, true, signalRef);
                                            if (thisLength > fullLength)
                                                fullLength = thisLength;
                                        }
                                    }
                                }
                            }
                        }
                    }

                    deltaLength = fullLength - Length;
                }

                // search back along route

                if (deltaLength < 0)
                {
                    float distance = fullLength + beginOffset;
                    bool platformFound = false;

                    for (int iIndex = firstRouteIndex - 1;
                                iIndex >= 0 && distance < 500f && platformFound;
                                iIndex--)
                    {
                        int nextSectionIndex = thisRoute[iIndex].TCSectionIndex;
                        TrackCircuitSection nextSection = signalRef.TrackCircuitList[nextSectionIndex];

                        foreach (int otherPlatformIndex in nextSection.PlatformIndex)
                        {
                            PlatformDetails otherPlatform = signalRef.PlatformDetailsList[otherPlatformIndex];
                            if (String.Compare(otherPlatform.Name, thisPlatform.Name) == 0)
                            {
                                fullLength = otherPlatform.Length + distance;
                                // we miss a little bit (offset) - that's because we don't know direction of other platform
                                platformFound = true; // only check for one more
                            }
                        }
                        distance += nextSection.Length;
                    }

                    deltaLength = fullLength - Length;
                }


                // determine stop position

                stopOffset = endOffset - (0.5f * deltaLength);

                // beyond section : check for route validity (may not exceed route)

                if (stopOffset > endSection.Length)
                {
                    float addOffset = stopOffset - endSection.Length;
                    float overlap = 0f;

                    for (int iIndex = lastRouteIndex; iIndex < thisRoute.Count && overlap < addOffset; iIndex++)
                    {
                        TrackCircuitSection nextSection = signalRef.TrackCircuitList[thisRoute[iIndex].TCSectionIndex];
                        overlap += nextSection.Length;
                    }

                    if (overlap < stopOffset)
                        stopOffset = overlap;
                }

                // check if stop offset beyond end signal - do not hold at signal

                int EndSignal = -1;
                bool HoldSignal = false;
                bool NoWaitSignal = false;
                bool NoClaimAllowed = false;

                // check if train is to reverse in platform
                // if so, set signal at other end as hold signal

                int useDirection = thisElement.Direction;
                bool inDirection = true;

                if (TCRoute.ReversalInfo[activeSubroute].Valid)
                {
                    TCReversalInfo thisReversal = TCRoute.ReversalInfo[activeSubroute];
                    int reversalIndex = thisReversal.SignalUsed ? thisReversal.LastSignalIndex : thisReversal.LastDivergeIndex;
                    if (reversalIndex >= 0 && reversalIndex <= lastRouteIndex) // reversal point is this section or earlier
                    {
                        useDirection = useDirection == 0 ? 1 : 0;
                        inDirection = false;
                    }
                }

                // check for end signal

                if (thisPlatform.EndSignals[useDirection] >= 0)
                {
                    EndSignal = thisPlatform.EndSignals[useDirection];

                    // stop location is in front of signal
                    if (inDirection)
                    {
                        if (thisPlatform.DistanceToSignals[useDirection] > (stopOffset - endOffset))
                        {
                            HoldSignal = true;

                            if ((thisPlatform.DistanceToSignals[useDirection] + (endOffset - stopOffset)) < clearingDistanceM)
                            {
                                stopOffset = endOffset + thisPlatform.DistanceToSignals[useDirection] - clearingDistanceM - 1.0f;
                            }
                        }
                        // if most of train fits in platform then stop at signal
                        else if ((thisPlatform.DistanceToSignals[useDirection] - clearingDistanceM + thisPlatform.Length) >
                                      (0.6 * Length))
                        {
                            HoldSignal = true;
                            stopOffset = endOffset + thisPlatform.DistanceToSignals[useDirection] - clearingDistanceM - 1.0f;
                            // set 1m earlier to give priority to station stop over signal
                        }
                        // train does not fit in platform - reset exit signal
                        else
                        {
                            EndSignal = -1;
                        }
                    }
                    else
                    // end of train is beyond signal
                    {
                        if ((beginOffset - thisPlatform.DistanceToSignals[useDirection]) < (stopOffset - Length))
                        {
                            HoldSignal = true;

                            if ((stopOffset - Length - beginOffset + thisPlatform.DistanceToSignals[useDirection]) < clearingDistanceM)
                            {
                                stopOffset = beginOffset - thisPlatform.DistanceToSignals[useDirection] + Length + clearingDistanceM + 1.0f;
                            }
                        }
                        // if most of train fits in platform then stop at signal
                        else if ((thisPlatform.DistanceToSignals[useDirection] - clearingDistanceM + thisPlatform.Length) >
                                      (0.6 * Length))
                        {
                            // set 1m earlier to give priority to station stop over signal
                            stopOffset = beginOffset - thisPlatform.DistanceToSignals[useDirection] + Length + clearingDistanceM + 1.0f;

                            // check if stop is clear of end signal (if any)
                            if (thisPlatform.EndSignals[thisElement.Direction] != -1)
                            {
                                if (stopOffset < (endOffset + thisPlatform.DistanceToSignals[thisElement.Direction]))
                                {
                                    HoldSignal = true; // if train fits between signals
                                }
                                else
                                {
                                    stopOffset = endOffset + thisPlatform.DistanceToSignals[thisElement.Direction] - 1.0f; // stop at end signal
                                }
                            }
                        }
                        // train does not fit in platform - reset exit signal
                        else
                        {
                            EndSignal = -1;
                        }
                    }
                }

                if (Simulator.Settings.NoForcedRedAtStationStops)
                {
                    // We don't want reds at exit signal in this case
                    HoldSignal = false;
                }

                // build and add station stop

                TCRouteElement lastElement = thisRoute[lastRouteIndex];

                StationStop thisStation = new StationStop(
                        platformStartID,
                        thisPlatform,
                        activeSubroute,
                        lastRouteIndex,
                        lastElement.TCSectionIndex,
                        thisElement.Direction,
                        EndSignal,
                        HoldSignal,
                        NoWaitSignal,
                        NoClaimAllowed,
                        stopOffset,
                        arrivalTime,
                        departTime,
                        false,
                        null,
                        null,
                        null,
                        false,
                        false,
                        false,
                        false,
                        false,
                        StationStop.STOPTYPE.STATION_STOP);

                thisStation.arrivalDT = arrivalDT;
                thisStation.departureDT = departureDT;

                return (thisStation);
            }
        }

        //================================================================================================//
        //
        // Set train route to alternative route - location based deadlock processing
        //

        public void ClearDeadlocks()
        {
            // clear deadlocks
            foreach (KeyValuePair<int, List<Dictionary<int, int>>> thisDeadlock in DeadlockInfo)
            {
#if DEBUG_DEADLOCK
                File.AppendAllText(@"C:\Temp\deadlock.txt", "\n === Removed Train : " + Number.ToString() + "\n");
                File.AppendAllText(@"C:\Temp\deadlock.txt", "Deadlock at section : " + thisDeadlock.Key.ToString() + "\n");
#endif
                TrackCircuitSection thisSection = signalRef.TrackCircuitList[thisDeadlock.Key];
                foreach (Dictionary<int, int> deadlockTrapInfo in thisDeadlock.Value)
                {
                    foreach (KeyValuePair<int, int> deadlockedTrain in deadlockTrapInfo)
                    {
                        Train otherTrain = GetOtherTrainByNumber(deadlockedTrain.Key);

#if DEBUG_DEADLOCK
                        File.AppendAllText(@"C:\Temp\deadlock.txt", "Other train index : " + deadlockedTrain.Key.ToString() + "\n");
                        if (otherTrain == null)
                        {
                            File.AppendAllText(@"C:\Temp\deadlock.txt", "Other train not found!" + "\n");
                        }
                        else
                        {
                            File.AppendAllText(@"C:\Temp\deadlock.txt", "CrossRef train info : " + "\n");
                            foreach (KeyValuePair<int, List<Dictionary<int, int>>> reverseDeadlock in otherTrain.DeadlockInfo)
                            {
                                File.AppendAllText(@"C:\Temp\deadlock.txt", "   " + reverseDeadlock.Key.ToString() + "\n");
                            }

                            foreach (KeyValuePair<int, List<Dictionary<int, int>>> reverseDeadlock in otherTrain.DeadlockInfo)
                            {
                                if (reverseDeadlock.Key == deadlockedTrain.Value)
                                {
                                    File.AppendAllText(@"C:\Temp\deadlock.txt", "Reverse Info : " + "\n");
                                    foreach (Dictionary<int, int> sectorList in reverseDeadlock.Value)
                                    {
                                        foreach (KeyValuePair<int, int> reverseInfo in sectorList)
                                        {
                                            File.AppendAllText(@"C:\Temp\deadlock.txt", "   " + reverseInfo.Key.ToString() + " + " + reverseInfo.Value.ToString() + "\n");
                                        }
                                    }
                                }
                            }
                        }
#endif
                        if (otherTrain != null && otherTrain.DeadlockInfo.ContainsKey(deadlockedTrain.Value))
                        {
                            List<Dictionary<int, int>> otherDeadlock = otherTrain.DeadlockInfo[deadlockedTrain.Value];
                            for (int iDeadlock = otherDeadlock.Count - 1; iDeadlock >= 0; iDeadlock--)
                            {
                                Dictionary<int, int> otherDeadlockInfo = otherDeadlock[iDeadlock];
                                if (otherDeadlockInfo.ContainsKey(Number)) otherDeadlockInfo.Remove(Number);
                                if (otherDeadlockInfo.Count <= 0) otherDeadlock.RemoveAt(iDeadlock);
                            }

                            if (otherDeadlock.Count <= 0)
                                otherTrain.DeadlockInfo.Remove(deadlockedTrain.Value);

                            if (otherTrain.DeadlockInfo.Count <= 0)
                                thisSection.ClearDeadlockTrap(otherTrain.Number);
                        }
                        TrackCircuitSection otherSection = signalRef.TrackCircuitList[deadlockedTrain.Value];
                        otherSection.ClearDeadlockTrap(Number);
                    }
                }
            }

            DeadlockInfo.Clear();
        }

        //================================================================================================//
        /// <summary>
        /// Get other train from number
        /// Use Simulator.Trains to get other train
        /// </summary>

        public Train GetOtherTrainByNumber(int reqNumber)
        {
            return Simulator.Trains.GetTrainByNumber(reqNumber);
        }

        //================================================================================================//
        /// <summary>
        /// Get other train from number
        /// Use Simulator.Trains to get other train
        /// </summary>

        public Train GetOtherTrainByName(string reqName)
        {
            return Simulator.Trains.GetTrainByName(reqName);
        }

        //================================================================================================//
        /// <summary>
        /// Update mininal delay - dummy method to allow virtualization by child classes
        /// <\summary>

        public virtual void UpdateMinimalDelay()
        {
        }

        //================================================================================================//
        /// <summary>
        /// Update AI Static state - dummy method to allow virtualization by child classes
        /// </summary>

        public virtual void UpdateAIStaticState(int presentTime)
        {
        }

        //================================================================================================//
        /// <summary>
        /// Get AI Movement State - dummy method to allow virtualization by child classes
        /// </summary>

        public virtual AITrain.AI_MOVEMENT_STATE GetAIMovementState()
        {
            return (AITrain.AI_MOVEMENT_STATE.UNKNOWN);
        }

        //================================================================================================//
        /// <summary>
        /// Check on station tasks, required when in timetable mode when there is no activity - dummy method to allow virtualization by child classes
        /// </summary>
        public virtual void CheckStationTask()
        {
        }

        //================================================================================================//
        /// <summary>
        /// Special additional methods when stopped at signal in timetable mode - dummy method to allow virtualization by child classes
        /// </summary>
        public virtual void ActionsForSignalStop(ref bool claimAllowed)
        {
        }

        //================================================================================================//
        /// <summary>
        /// Check on attach state, required when in timetable mode for player train - dummy method to allow virtualization by child classes
        /// </summary>
        public virtual void CheckPlayerAttachState()
        {
        }

        //================================================================================================//
        //
        // Check if train is in wait mode - dummy method to allow virtualization by child classes
        //

        public virtual bool isInWaitState()
        {
            return (false);
        }

        //================================================================================================//
        //
        // Check if train has AnyWait valid for this section - dummy method to allow virtualization by child classes
        //

        public virtual bool CheckAnyWaitCondition(int index)
        {
            return (false);
        }

        //================================================================================================//
        //
        // Check if train has Wait valid for this section - dummy method to allow virtualization by child classes
        //

        public virtual bool HasActiveWait(int startSectionIndex, int endSectionIndex)
        {
            return (false);
        }

        //================================================================================================//
        /// <summary>
        /// Update Section State - additional
        /// dummy method to allow virtualisation for Timetable trains
        /// </summary>

        public virtual void UpdateSectionState_Additional(int sectionIndex)
        {
        }

        //================================================================================================//
        /// <summary>
        /// Check wait condition
        /// Dummy method to allow virtualization by child classes
        /// <\summary>

        public virtual bool CheckWaitCondition(int sectionIndex)
        {
            return (false);
        }

        //================================================================================================//
        /// <summary>
        /// Check Pool Access
        /// Dummy method to allow virtualization by child classes
        /// <\summary>

        public virtual bool CheckPoolAccess(int sectionIndex)
        {
            return (false);
        }

        //================================================================================================//
        /// <summary>
        /// Check if deadlock must be accepted
        /// Dummy method to allow virtualization by child classes
        /// <\summary>

        public virtual bool VerifyDeadlock(List<int> deadlockReferences)
        {
            return (true);
        }

        //================================================================================================//
        /// <summary>
        /// TestAbsDelay
        /// Tests if Waiting point delay >=30000 and <4000; under certain conditions this means that
        /// delay represents an absolute time of day, with format 3HHMM
        /// </summary>
        /// 
        public virtual void TestAbsDelay(ref int delay, int correctedTime)
        {
            if (!Simulator.Settings.ExtendedAIShunting) return;
            if (delay < 30000 || delay >= 40000) return;
            int hour = (delay / 100) % 100;
            int minute = delay % 100;
            int waitUntil = 60 * (minute + 60 * hour);
            int latest = CompareTimes.LatestTime(waitUntil, correctedTime);
            if (latest == waitUntil && waitUntil >= correctedTime) delay = waitUntil - correctedTime;
            else if (latest == correctedTime) delay = 1; // put 1 second delay if waitUntil is already over
            else delay = waitUntil - correctedTime + 3600 * 24; // we are over midnight here
        }

        //================================================================================================//
        /// <summary>
        /// ToggleDoors
        /// Toggles status of doors of a train
        /// Parameters: right = true if right doors; open = true if opening
        /// <\summary>
        public void ToggleDoors(bool right, bool open)
        {
            foreach (TrainCar car in Cars)
            {
                var mstsWagon = car as MSTSWagon;
                    if (!car.Flipped && right || car.Flipped && !right)
                    {
                        mstsWagon.DoorRightOpen = open;
                    }
                    else
                    {
                        mstsWagon.DoorLeftOpen = open;
                    }
                mstsWagon.SignalEvent(open? Event.DoorOpen : Event.DoorClose); // hook for sound trigger
            }
        }

        //================================================================================================//
        /// <summary>
        /// Check if it's time to have a failed car or locomotive
        /// </summary>
        /// 

        public void CheckFailures (float elapsedClockSeconds)
        {
            if ( IsFreight ) CheckBrakes(elapsedClockSeconds);
            CheckLocoPower(elapsedClockSeconds);
        }

        //================================================================================================//
        /// <summary>
        /// Check if it's time to have a car with stuck brakes
        /// </summary>

        public void CheckBrakes (float elapsedClockSeconds)
        {
            if (BrakingTime == -1) return;
            if (BrakingTime == -2)
            {
                BrakingTime = -1; // Viewer has seen it, can pass to this value
                return;
            }
            if (SpeedMpS > 0)
            {
                for (int iCar = 0; iCar < Cars.Count; iCar++)
                {
                    var car = Cars[iCar];
                    if (!(car is MSTSLocomotive))
                    {
                        if (car.BrakeSystem.IsBraking() && BrakingTime >= 0)
                        {
                            BrakingTime += elapsedClockSeconds;
                            ContinuousBrakingTime += elapsedClockSeconds;
                            if (BrakingTime >= 1200.0f/ Simulator.Settings.ActRandomizationLevel || ContinuousBrakingTime >= 600.0f / Simulator.Settings.ActRandomizationLevel)
                            {
                                var randInt = Simulator.Random.Next(200000);
                                var brakesStuck = false;
                                if (randInt > 200000 - (Simulator.Settings.ActRandomizationLevel == 1 ? 4 : Simulator.Settings.ActRandomizationLevel == 2 ? 8 : 31))
                                // a car will have brakes stuck. Select which one
                                {
                                    var iBrakesStuckCar = Simulator.Random.Next(Cars.Count);
                                    var jBrakesStuckCar = iBrakesStuckCar;
                                    while (Cars[iBrakesStuckCar] is MSTSLocomotive && iBrakesStuckCar < Cars.Count)
                                        iBrakesStuckCar++;
                                    if (iBrakesStuckCar != Cars.Count)
                                    {
                                        brakesStuck = true;
                                    }
                                    else
                                    {
                                        while (Cars[jBrakesStuckCar] is MSTSLocomotive && jBrakesStuckCar > Cars.Count)
                                            jBrakesStuckCar--;
                                        if (jBrakesStuckCar != -1)
                                        {
                                            iBrakesStuckCar = jBrakesStuckCar;
                                            brakesStuck = true;
                                        }
                                    }
                                    if (brakesStuck)
                                    {
                                        Cars[iBrakesStuckCar].BrakesStuck = true;
                                        BrakingTime = -2; //Check no more, we already have a brakes stuck car
                                        ContinuousBrakingTime = -iBrakesStuckCar; // let's use it for two purposes
                                        Simulator.Confirmer.Warning(Simulator.Catalog.GetString("Car " + Cars[iBrakesStuckCar].CarID + " has stuck brakes"));
                                    }
                                }
                            }
                        }
                        else ContinuousBrakingTime = 0;
                        return;
                    }
                }
            }
        }

        //================================================================================================//
        /// <summary>
        /// Check if it's time to have an electric or diesel loco with a bogie not powering
        /// </summary>

        public void CheckLocoPower(float elapsedClockSeconds)
        {
            if (RunningTime == -1) return;
            if (RunningTime == -2)
            {
                RunningTime = -1; // Viewer has seen it, can pass to this value
                return;
            }
            if (SpeedMpS > 0)
            {
                var oldRunningTime = RunningTime;
                RunningTime += elapsedClockSeconds;
                if (Math.Truncate(oldRunningTime) < Math.Truncate(RunningTime)) // Check only every second
                {
                    var nLocos = 0;
                    for (int iCar = 0; iCar < Cars.Count; iCar++)
                    {
                        var car = Cars[iCar];
                        if ((car is MSTSElectricLocomotive || car is MSTSDieselLocomotive) && car.Parts.Count >= 2 &&
                            ((car as MSTSLocomotive).ThrottlePercent > 10 || (car as MSTSLocomotive).DynamicBrakePercent > 10)) nLocos++;
                    }
                    if (nLocos > 0)
                    {
                        var randInt = Simulator.Random.Next(2000000 / nLocos);
                        var locoUnpowered = false;
                        if (randInt > 2000000 / nLocos - (Simulator.Settings.ActRandomizationLevel == 1 ? 2 : Simulator.Settings.ActRandomizationLevel == 2 ? 8 : 50))
                        // a loco will be partly or totally unpowered. Select which one
                        {
                            var iLocoUnpoweredCar = Simulator.Random.Next(Cars.Count);
                            var jLocoUnpoweredCar = iLocoUnpoweredCar;
                            if (iLocoUnpoweredCar % 2 == 1)
                            {
                                locoUnpowered = SearchBackOfTrain(ref iLocoUnpoweredCar);
                                if (!locoUnpowered)
                                {
                                    iLocoUnpoweredCar = jLocoUnpoweredCar;
                                    locoUnpowered = SearchFrontOfTrain(ref iLocoUnpoweredCar);
                                }

                            }
                            else
                            {
                                locoUnpowered = SearchFrontOfTrain(ref iLocoUnpoweredCar);
                                if (!locoUnpowered)
                                {
                                    iLocoUnpoweredCar = jLocoUnpoweredCar;
                                    locoUnpowered = SearchBackOfTrain(ref iLocoUnpoweredCar);
                                }
                            }

                            if (locoUnpowered)
                            {
                                RunningTime = -2; //Check no more, we already have an unpowered loco
                                var unpoweredLoco = Cars[iLocoUnpoweredCar] as MSTSLocomotive;
                                if (randInt % 2 == 1 || unpoweredLoco is MSTSElectricLocomotive)
                                {
                                    unpoweredLoco.PowerReduction = 0.5f;
                                    Simulator.Confirmer.Warning(Simulator.Catalog.GetString("Locomotive " + unpoweredLoco.CarID + " partial failure: 1 unpowered bogie"));
                                }
                                else
                                {
                                    unpoweredLoco.PowerReduction = 1.0f;
                                    Simulator.Confirmer.Warning(Simulator.Catalog.GetString("Locomotive " + unpoweredLoco.CarID + " compressor blown"));
                                }
                                UnpoweredLoco = iLocoUnpoweredCar;
                            }
                        }
                    }
                }
            }
        }

        //================================================================================================//
        /// <summary>
        /// Check first electric or diesel loco searching towards back of train
        /// </summary>
        
        private bool SearchBackOfTrain( ref int iLocoUnpoweredCar)
        {
            var locoUnpowered = false;
            while (iLocoUnpoweredCar < Cars.Count && !((Cars[iLocoUnpoweredCar] is MSTSElectricLocomotive || Cars[iLocoUnpoweredCar] is MSTSDieselLocomotive) && Cars[iLocoUnpoweredCar].Parts.Count >= 2))
                iLocoUnpoweredCar++;
            if (iLocoUnpoweredCar != Cars.Count)
            {
                locoUnpowered = true;
            }

            return locoUnpowered;
        }

        //================================================================================================//
        /// <summary>
        /// Check first electric or diesel loco searching towards front of train
        /// </summary>

        private bool SearchFrontOfTrain(ref int iLocoUnpoweredCar)
        {

            var locoUnpowered = false;
            while (iLocoUnpoweredCar >= 0 && !((Cars[iLocoUnpoweredCar] is MSTSElectricLocomotive || Cars[iLocoUnpoweredCar] is MSTSDieselLocomotive) && Cars[iLocoUnpoweredCar].Parts.Count >= 2))
                iLocoUnpoweredCar--;
            if (iLocoUnpoweredCar != -1)
            {
                locoUnpowered = true;
            }
            return locoUnpowered;
        }

        //================================================================================================//
        /// <summary>
        /// Routed train class : train class plus valid route direction indication
        /// Used throughout in the signalling process in order to derive correct route in Manual and Explorer modes
        /// </summary>

        public class TrainRouted
        {
            public Train Train;
            public int TrainRouteDirectionIndex;

            //================================================================================================//
            /// <summary>
            /// Constructor
            /// </summary>

            public TrainRouted(Train thisTrain, int thisIndex)
            {
                Train = thisTrain;
                TrainRouteDirectionIndex = thisIndex;
            }
        }

        //================================================================================================//
        /// <summary>
        /// Track Circuit Route Path
        /// </summary>

        public class TCRoutePath
        {
            public List<TCSubpathRoute> TCRouteSubpaths = new List<TCSubpathRoute>();
            public List<TCSubpathRoute> TCAlternativePaths = new List<TCSubpathRoute>();
            public int activeSubpath;
            public int activeAltpath;
            public List<int[]> WaitingPoints = new List<int[]>(); // [0] = sublist in which WP is placed; 
            // [1] = WP section; [2] = WP wait time (delta); [3] = WP depart time;
            // [4] = hold signal
            public List<TCReversalInfo> ReversalInfo = new List<TCReversalInfo>();
            public List<RoughReversalInfo> RoughReversalInfos = new List<RoughReversalInfo>();
            public List<int> LoopEnd = new List<int>();
            public Dictionary<string, int[]> StationXRef = new Dictionary<string, int[]>();
            // int[0] = subpath index, int[1] = element index, int[2] = platform ID
            public int OriginalSubpath = -1; // reminds original subpath when train manually rerouted

            //================================================================================================//
            /// <summary>
            /// Constructor (from AIPath)
            /// </summary>

            public TCRoutePath(AIPath aiPath, int orgDir, float thisTrainLength, Signals orgSignals, int trainNumber, UserSettings settings)
            {
                activeSubpath = 0;
                activeAltpath = -1;
                float offset = 0;

                //
                // collect all TC Elements
                //
                // get tracknode from first path node
                //
                int sublist = 0;

                Dictionary<int, int[]> AlternativeRoutes = new Dictionary<int, int[]>();
                Queue<int> ActiveAlternativeRoutes = new Queue<int>();

                //  Create the first TCSubpath into the TCRoute
                TCSubpathRoute thisSubpath = new TCSubpathRoute();
                TCRouteSubpaths.Add(thisSubpath);

                int currentDir = orgDir;
                int newDir = orgDir;

                List<float> reversalOffset = new List<float>();
                List<int> reversalIndex = new List<int>();

                //
                // if original direction not set, determine it through first switch
                //

                if (orgDir < -1)
                {
                    bool firstSwitch = false;
                    int prevTNode = 0;
                    int jnDir = 0;

                    for (int iPNode = 0; iPNode < aiPath.Nodes.Count - 1 && !firstSwitch; iPNode++)
                    {
                        AIPathNode pNode = aiPath.Nodes[iPNode];
                        if (pNode.JunctionIndex > 0)
                        {
                            TrackNode jn = aiPath.TrackDB.TrackNodes[pNode.JunctionIndex];
                            firstSwitch = true;
                            for (int iPin = 0; iPin < jn.TrPins.Length; iPin++)
                            {
                                if (jn.TrPins[iPin].Link == prevTNode)
                                {
                                    jnDir = jn.TrPins[iPin].Direction == 1 ? 0 : 1;
                                }
                            }
                        }
                        else
                        {
                            if (pNode.Type == AIPathNodeType.Other)
                                prevTNode = pNode.NextMainTVNIndex;
                        }
                    }

                    currentDir = jnDir;
                }

                //
                // loop through path nodes
                //

                AIPathNode thisPathNode = aiPath.Nodes[0];
                AIPathNode nextPathNode = null;
                AIPathNode lastPathNode = null;

                int trackNodeIndex = thisPathNode.NextMainTVNIndex;
                TrackNode thisNode = null;

                thisPathNode = thisPathNode.NextMainNode;
                int reversal = 0;

                while (thisPathNode != null)
                {
                    lastPathNode = thisPathNode;

                    // process siding items

                    if (thisPathNode.Type == AIPathNodeType.SidingStart)
                    {
                        TrackNode sidingNode = aiPath.TrackDB.TrackNodes[thisPathNode.JunctionIndex];
                        int startTCSectionIndex = sidingNode.TCCrossReference[0].Index;
                        int[] altRouteReference = new int[3];
                        altRouteReference[0] = sublist;
                        altRouteReference[1] = thisPathNode.Index;
                        altRouteReference[2] = -1;
                        AlternativeRoutes.Add(startTCSectionIndex, altRouteReference);
                        ActiveAlternativeRoutes.Enqueue(startTCSectionIndex);

                        thisPathNode.Type = AIPathNodeType.Other;
                    }
                    else if (thisPathNode.Type == AIPathNodeType.SidingEnd)
                    {
                        TrackNode sidingNode = aiPath.TrackDB.TrackNodes[thisPathNode.JunctionIndex];
                        int endTCSectionIndex = sidingNode.TCCrossReference[0].Index;

                        int refStartIndex = ActiveAlternativeRoutes.Dequeue();
                        int[] altRouteReference = AlternativeRoutes[refStartIndex];
                        altRouteReference[2] = endTCSectionIndex;

                        thisPathNode.Type = AIPathNodeType.Other;
                    }

                    //
                    // process last non-junction section
                    //

                    if (thisPathNode.Type == AIPathNodeType.Other)
                    {
                        thisNode = aiPath.TrackDB.TrackNodes[trackNodeIndex];

                        //  SPA:    Subpath:    Add TCRouteElement for each TrackCircuitsection in node
                        if (currentDir == 0)
                        {
                            for (int iTC = 0; iTC < thisNode.TCCrossReference.Count; iTC++)
                            {
                                TCRouteElement thisElement =
                                    new TCRouteElement(thisNode, iTC, currentDir, orgSignals);
                                thisSubpath.Add(thisElement);
                                SetStationReference(TCRouteSubpaths, thisElement.TCSectionIndex, orgSignals);
                            }
                            newDir = thisNode.TrPins[currentDir].Direction;

                        }
                        else
                        {
                            for (int iTC = thisNode.TCCrossReference.Count - 1; iTC >= 0; iTC--)
                            {
                                TCRouteElement thisElement =
                                    new TCRouteElement(thisNode, iTC, currentDir, orgSignals);
                                thisSubpath.Add(thisElement);
                                SetStationReference(TCRouteSubpaths, thisElement.TCSectionIndex, orgSignals);
                            }
                            newDir = thisNode.TrPins[currentDir].Direction;
                        }

                        if (reversal > 0)
                        {
                            while (reversal > 0)
                            {
                                //<CSComment> following block can be uncommented if it is preferred to leave in the path the double reverse points
                                //                                if (!Simulator.TimetableMode && Simulator.Settings.EnhancedActCompatibility && sublist > 0 &&
                                //                                    TCRouteSubpaths[sublist].Count <= 0)
                                //                                {
                                //                                    // check if preceding subpath has no sections, and in such case insert the one it should have,
                                //                                    // taking the last section from the preceding subpath
                                //                                    thisNode = aiPath.TrackDB.TrackNodes[trackNodeIndex];
                                //                                    if (currentDir == 0)
                                //                                    {
                                //                                        for (int iTC = 0; iTC < thisNode.TCCrossReference.Count; iTC++)
                                //                                        {
                                //                                            if (thisNode.TCCrossReference[iTC].Index == RoughReversalInfos[sublist].ReversalSectionIndex)
                                //                                            {
                                //                                                TCRouteElement thisElement =
                                //                                                     new TCRouteElement(thisNode, iTC, currentDir, orgSignals);
                                //                                                thisSubpath.Add(thisElement);
                                //                                                //  SPA:    Station:    A adapter, 
                                //                                                SetStationReference(TCRouteSubpaths, thisElement.TCSectionIndex, orgSignals);
                                //                                                break;
                                //                                            }
                                //                                        }
                                //                                        newDir = thisNode.TrPins[currentDir].Direction;
                                //
                                //                                    }
                                //                                    else
                                //                                    {
                                //                                        for (int iTC = thisNode.TCCrossReference.Count - 1; iTC >= 0; iTC--)
                                //                                        {
                                //                                            if (thisNode.TCCrossReference[iTC].Index == RoughReversalInfos[sublist].ReversalSectionIndex)
                                //                                            {
                                //                                                TCRouteElement thisElement =
                                //                                                   new TCRouteElement(thisNode, iTC, currentDir, orgSignals);
                                //                                                thisSubpath.Add(thisElement);
                                //                                                SetStationReference(TCRouteSubpaths, thisElement.TCSectionIndex, orgSignals);
                                //                                                break;
                                //                                            }
                                //                                        }
                                //                                        newDir = thisNode.TrPins[currentDir].Direction;
                                //                                    }
                                //                                }

                                sublist++;
                                thisSubpath = new TCSubpathRoute();
                                TCRouteSubpaths.Add(thisSubpath);
                                currentDir = currentDir == 1 ? 0 : 1;
                                reversal--;        // reset reverse point
                            }
                            continue;          // process this node again in reverse direction
                        }
                        //  SPA:    WP: New forms 

                        //
                        // process junction section
                        //

                        if (thisPathNode.JunctionIndex > 0)
                        {
                            TrackNode junctionNode = aiPath.TrackDB.TrackNodes[thisPathNode.JunctionIndex];
                            TCRouteElement thisElement =
                                new TCRouteElement(junctionNode, 0, newDir, orgSignals);
                            thisSubpath.Add(thisElement);

                            trackNodeIndex = thisPathNode.NextMainTVNIndex;

                            if (thisPathNode.IsFacingPoint)   // exit is one of two switch paths //
                            {
                                uint firstpin = (junctionNode.Inpins > 1) ? 0 : junctionNode.Inpins;
                                if (junctionNode.TrPins[firstpin].Link == trackNodeIndex)
                                {
                                    newDir = junctionNode.TrPins[firstpin].Direction;
                                    thisElement.OutPin[1] = 0;
                                }
                                else
                                {
                                    firstpin++;
                                    newDir = junctionNode.TrPins[firstpin].Direction;
                                    thisElement.OutPin[1] = 1;
                                }
                            }
                            else  // exit is single path //
                            {
                                uint firstpin = (junctionNode.Inpins > 1) ? junctionNode.Inpins : 0;
                                newDir = junctionNode.TrPins[firstpin].Direction;
                            }
                        }

                        currentDir = newDir;

                        //
                        // find next junction path node
                        //

                        nextPathNode = thisPathNode.NextMainNode;
                    }
                    else
                    {
                        nextPathNode = thisPathNode;
                    }

                    while (nextPathNode != null && nextPathNode.JunctionIndex < 0)
                    {
                        lastPathNode = nextPathNode;

                        if (nextPathNode.Type == AIPathNodeType.Reverse)
                        {
                            TrackNode reversalNode = aiPath.TrackDB.TrackNodes[nextPathNode.NextMainTVNIndex];
                            TrVectorSection firstSection = reversalNode.TrVectorNode.TrVectorSections[0];
                            Traveller TDBTrav = new Traveller(aiPath.TSectionDat, aiPath.TrackDB.TrackNodes, reversalNode,
                                            firstSection.TileX, firstSection.TileZ,
                                            firstSection.X, firstSection.Z, (Traveller.TravellerDirection)1);
                            offset = TDBTrav.DistanceTo(reversalNode,
                                nextPathNode.Location.TileX, nextPathNode.Location.TileZ,
                                nextPathNode.Location.Location.X,
                                nextPathNode.Location.Location.Y,
                                nextPathNode.Location.Location.Z);
                            float reverseOffset = 0;
                            int sectionIndex = -1;
                            int validDir = currentDir;
                            if (reversal % 2 == 1) validDir = validDir == 1 ? 0 : 1;
                            if (validDir == 0)
                            {
                                reverseOffset = -offset;
                                for (int i = reversalNode.TCCrossReference.Count - 1; i >= 0 && reverseOffset <= 0; i--)
                                {
                                    reverseOffset += reversalNode.TCCrossReference[i].Length;
                                    sectionIndex = reversalNode.TCCrossReference[i].Index;

                                }
                            }
                            else
                            {
                                int exti = 0;
                                reverseOffset = offset;
                                for (int i = reversalNode.TCCrossReference.Count - 1; i >= 0 && reverseOffset >= 0; i--)
                                {
                                    reverseOffset -= reversalNode.TCCrossReference[i].Length;
                                    sectionIndex = reversalNode.TCCrossReference[i].Index;
                                    exti = i;
                                }
                                reverseOffset += reversalNode.TCCrossReference[exti].Length;
                            }
                            RoughReversalInfo roughReversalInfo = new RoughReversalInfo(sublist + reversal, reverseOffset, sectionIndex);
                            RoughReversalInfos.Add(roughReversalInfo);
                            reversalOffset.Add(offset);
                            reversalIndex.Add(sublist);
                            reversal++;
                        }
                        else if (nextPathNode.Type == AIPathNodeType.Stop)
                        {
                            int validDir = currentDir;
                            if (reversal % 2 == 1) validDir = validDir == 1 ? 0 : 1;
                            offset = GetOffsetToPathNode(aiPath, validDir, nextPathNode);
                            int[] waitingPoint = new int[6];
                            waitingPoint[0] = sublist + reversal;
                            waitingPoint[1] = ConvertWaitingPoint(nextPathNode, aiPath.TrackDB, aiPath.TSectionDat, currentDir);

                            waitingPoint[2] = nextPathNode.WaitTimeS;
                            waitingPoint[3] = nextPathNode.WaitUntil;
                            waitingPoint[4] = -1; // hold signal set later
                            waitingPoint[5] = (int)offset;
                            WaitingPoints.Add(waitingPoint);
                        }

                        // other type of path need not be processed

                        // go to next node
                        nextPathNode = nextPathNode.NextMainNode;
                    }
                    thisPathNode = nextPathNode;
                }

                if (!orgSignals.Simulator.TimetableMode)
                {
                    // insert reversals when they are in last section
                    while (reversal > 0)
                    {
                        thisNode = aiPath.TrackDB.TrackNodes[trackNodeIndex];
                        if (currentDir == 0)
                        {
                            for (int iTC = 0; iTC < thisNode.TCCrossReference.Count; iTC++)
                            {
                                TCRouteElement thisElement =
                                  new TCRouteElement(thisNode, iTC, currentDir, orgSignals);
                                thisSubpath.Add(thisElement);
                                //  SPA:    Station:    A adapter, 
                                SetStationReference(TCRouteSubpaths, thisElement.TCSectionIndex, orgSignals);
                                if (thisNode.TCCrossReference[iTC].Index == RoughReversalInfos[sublist].ReversalSectionIndex)
                                {
                                     break;
                                }
                            }
                            newDir = thisNode.TrPins[currentDir].Direction;

                        }
                        else
                        {
                            for (int iTC = thisNode.TCCrossReference.Count - 1; iTC >= 0; iTC--)
                            {
                                TCRouteElement thisElement =
                                    new TCRouteElement(thisNode, iTC, currentDir, orgSignals);
                                thisSubpath.Add(thisElement);
                                SetStationReference(TCRouteSubpaths, thisElement.TCSectionIndex, orgSignals);
                                if (thisNode.TCCrossReference[iTC].Index == RoughReversalInfos[sublist].ReversalSectionIndex)
                                {
                                     break;
                                }
                            }
                            newDir = thisNode.TrPins[currentDir].Direction;
                        }
                        sublist++;
                        thisSubpath = new TCSubpathRoute();
                        TCRouteSubpaths.Add(thisSubpath);
                        currentDir = currentDir == 1 ? 0 : 1;
                        reversal--;        // reset reverse point
                    }
                }
                //
                // add last section
                //

                thisNode = aiPath.TrackDB.TrackNodes[trackNodeIndex];
                TrVectorSection endFirstSection = thisNode.TrVectorNode.TrVectorSections[0];
                Traveller TDBEndTrav = new Traveller(aiPath.TSectionDat, aiPath.TrackDB.TrackNodes, thisNode,
                                endFirstSection.TileX, endFirstSection.TileZ,
                                endFirstSection.X, endFirstSection.Z, (Traveller.TravellerDirection)1);
                float endOffset = TDBEndTrav.DistanceTo(thisNode,
                    lastPathNode.Location.TileX, lastPathNode.Location.TileZ,
                    lastPathNode.Location.Location.X,
                    lastPathNode.Location.Location.Y,
                    lastPathNode.Location.Location.Z);

                // Prepare info about route end point
                float reverseEndOffset = 0;
                int endNodeSectionIndex = -1;
                if (currentDir == 0)
                {
                    reverseEndOffset = -endOffset;
                    for (int i = thisNode.TCCrossReference.Count - 1; i >= 0 && reverseEndOffset <= 0; i--)
                    {
                        reverseEndOffset += thisNode.TCCrossReference[i].Length;
                        endNodeSectionIndex = thisNode.TCCrossReference[i].Index;

                    }
                }
                else
                {
                    int exti = 0;
                    reverseEndOffset = endOffset;
                    for (int i = thisNode.TCCrossReference.Count - 1; i >= 0 && reverseEndOffset >= 0; i--)
                    {
                        reverseEndOffset -= thisNode.TCCrossReference[i].Length;
                        endNodeSectionIndex = thisNode.TCCrossReference[i].Index;
                        exti = i;
                    }
                    reverseEndOffset += thisNode.TCCrossReference[exti].Length;
                }
                RoughReversalInfo lastReversalInfo = new RoughReversalInfo(sublist, reverseEndOffset, endNodeSectionIndex);
                RoughReversalInfos.Add(lastReversalInfo);

                // only add last section if end point is in different tracknode as last added item
                if (thisSubpath.Count <= 0 ||
                    thisNode.Index != orgSignals.TrackCircuitList[thisSubpath[thisSubpath.Count - 1].TCSectionIndex].OriginalIndex)
                {
                    if (currentDir == 0)
                    {
                        for (int iTC = 0; iTC < thisNode.TCCrossReference.Count; iTC++)
                        {
                            if ((thisNode.TCCrossReference[iTC].OffsetLength[1] + thisNode.TCCrossReference[iTC].Length) > endOffset)
                            //                      if (thisNode.TCCrossReference[iTC].Position[0] < endOffset)
                            {
                                TCRouteElement thisElement =
                                    new TCRouteElement(thisNode, iTC, currentDir, orgSignals);
                                if (thisSubpath.Count <= 0 || thisSubpath[thisSubpath.Count - 1].TCSectionIndex != thisElement.TCSectionIndex)
                                {
                                    thisSubpath.Add(thisElement); // only add if not yet set
                                    SetStationReference(TCRouteSubpaths, thisElement.TCSectionIndex, orgSignals);
                                }
                            }
                        }
                    }
                    else
                    {
                        for (int iTC = thisNode.TCCrossReference.Count - 1; iTC >= 0; iTC--)
                        {
                            if (thisNode.TCCrossReference[iTC].OffsetLength[1] < endOffset)
                            {
                                TCRouteElement thisElement =
                                new TCRouteElement(thisNode, iTC, currentDir, orgSignals);
                                if (thisSubpath.Count <= 0 || thisSubpath[thisSubpath.Count - 1].TCSectionIndex != thisElement.TCSectionIndex)
                                {
                                    thisSubpath.Add(thisElement); // only add if not yet set
                                    SetStationReference(TCRouteSubpaths, thisElement.TCSectionIndex, orgSignals);
                                }
                            }
                        }
                    }
                }

                // check if section extends to end of track

                TCRouteElement lastElement = thisSubpath[thisSubpath.Count - 1];
                TrackCircuitSection lastEndSection = orgSignals.TrackCircuitList[lastElement.TCSectionIndex];
                int lastDirection = lastElement.Direction;

                List<TCRouteElement> addedElements = new List<TCRouteElement>();
                if (lastEndSection.CircuitType != TrackCircuitSection.TrackCircuitType.EndOfTrack && lastEndSection.EndSignals[lastDirection] == null)
                {
                    int thisDirection = lastDirection;
                    lastDirection = lastEndSection.Pins[thisDirection, 0].Direction;
                    lastEndSection = orgSignals.TrackCircuitList[lastEndSection.Pins[thisDirection, 0].Link];

                    while (lastEndSection.CircuitType == TrackCircuitSection.TrackCircuitType.Normal && lastEndSection.EndSignals[lastDirection] == null)
                    {
                        addedElements.Add(new TCRouteElement(lastEndSection.Index, lastDirection));
                        thisDirection = lastDirection;
                        lastDirection = lastEndSection.Pins[thisDirection, 0].Direction;
                        lastEndSection = orgSignals.TrackCircuitList[lastEndSection.Pins[thisDirection, 0].Link];
                    }

                    if (lastEndSection.CircuitType == TrackCircuitSection.TrackCircuitType.EndOfTrack)
                    {
                        foreach (TCRouteElement addedElement in addedElements)
                        {
                            thisSubpath.Add(addedElement);
                            SetStationReference(TCRouteSubpaths, addedElement.TCSectionIndex, orgSignals);
                        }
                        thisSubpath.Add(new TCRouteElement(lastEndSection.Index, lastDirection));
                    }
                }

                // remove sections beyond reversal points

                for (int iSub = 0; iSub < reversalOffset.Count; iSub++)  // no reversal for final path
                {
                    TCSubpathRoute revSubPath = TCRouteSubpaths[reversalIndex[iSub]];
                    offset = reversalOffset[iSub];
                    if (revSubPath.Count <= 0)
                        continue;

                    int direction = revSubPath[revSubPath.Count - 1].Direction;

                    bool withinOffset = true;
                    List<int> removeSections = new List<int>();
                    int lastSectionIndex = revSubPath.Count - 1;

                    // create list of sections beyond reversal point 

                    if (direction == 0)
                    {
                        for (int iSection = revSubPath.Count - 1; iSection > 0 && withinOffset; iSection--)
                        {
                            TrackCircuitSection thisSection = orgSignals.TrackCircuitList[revSubPath[iSection].TCSectionIndex];
                            if (thisSection.CircuitType == TrackCircuitSection.TrackCircuitType.Junction)
                            {
                                withinOffset = false;    // always end on junction (next node)
                            }
                            else if (thisSection.CircuitType == TrackCircuitSection.TrackCircuitType.Crossover)
                            {
                                removeSections.Add(iSection);        // always remove crossover if last section was removed
                                lastSectionIndex = iSection - 1;
                            }
                            else if (thisSection.OffsetLength[1] + thisSection.Length < offset) // always use offsetLength[1] as offset is wrt begin of original section
                            {
                                removeSections.Add(iSection);
                                lastSectionIndex = iSection - 1;
                            }
                            else
                            {
                                withinOffset = false;
                            }
                        }
                    }
                    else
                    {
                        for (int iSection = revSubPath.Count - 1; iSection > 0 && withinOffset; iSection--)
                        {
                            TrackCircuitSection thisSection = orgSignals.TrackCircuitList[revSubPath[iSection].TCSectionIndex];
                            if (thisSection.CircuitType == TrackCircuitSection.TrackCircuitType.Junction)
                            {
                                withinOffset = false;     // always end on junction (next node)
                            }
                            else if (thisSection.CircuitType == TrackCircuitSection.TrackCircuitType.Crossover)
                            {
                                removeSections.Add(iSection);        // always remove crossover if last section was removed
                                lastSectionIndex = iSection - 1;
                            }
                            else if (thisSection.OffsetLength[1] > offset)
                            {
                                removeSections.Add(iSection);
                                lastSectionIndex = iSection - 1;
                            }
                            else
                            {
                                withinOffset = false;
                            }
                        }
                    }

                    // extend route to first signal or first node

                    bool signalFound = false;

                    for (int iSection = lastSectionIndex; iSection < revSubPath.Count - 1 && !signalFound; iSection++)
                    {
                        TrackCircuitSection thisSection = orgSignals.TrackCircuitList[revSubPath[iSection].TCSectionIndex];
                        removeSections.Remove(iSection);
                        if (thisSection.EndSignals[direction] != null)
                        {
                            signalFound = true;
                        }
                    }

                    // remove sections beyond first signal or first node from reversal point

                    for (int iSection = 0; iSection < removeSections.Count; iSection++)
                    {
                        revSubPath.RemoveAt(removeSections[iSection]);
                    }
                }

                // remove dummy subpaths (from double reversion)

                List<int> subRemoved = new List<int>();
                int orgCount = TCRouteSubpaths.Count;
                int removed = 0;
                Dictionary<int, int> newIndices = new Dictionary<int, int>();

                for (int iSub = TCRouteSubpaths.Count - 1; iSub >= 0; iSub--)
                {
                    if (TCRouteSubpaths[iSub].Count <= 0)
                    {
                        TCRouteSubpaths.RemoveAt(iSub);
                        subRemoved.Add(iSub);
                        var itemToRemove = RoughReversalInfos.FindIndex(r => r.SubPathIndex >= iSub);
                        if (itemToRemove != -1)
                        {
                            if (RoughReversalInfos[itemToRemove].SubPathIndex == iSub) RoughReversalInfos.RemoveAt(itemToRemove);
                            for (int i = itemToRemove; i < RoughReversalInfos.Count; i++)
                            {
                                RoughReversalInfos[i].SubPathIndex--;
                            }
                        }
                    }
                }

                // calculate new indices
                for (int iSub = 0; iSub <= orgCount - 1; iSub++) //<CSComment> maybe comparison only with less than?
                {
                    newIndices.Add(iSub, iSub - removed);
                    if (subRemoved.Contains(iSub))
                    {
                        removed++;
                    }
                }

                // if removed, update indices of waiting points
                if (removed > 0)
                {
                    foreach (int[] thisWP in WaitingPoints)
                    {
                        thisWP[0] = newIndices[thisWP[0]];
                    }

                    // if remove, update indices of alternative paths
                    Dictionary<int, int[]> copyAltRoutes = AlternativeRoutes;
                    AlternativeRoutes.Clear();
                    foreach (KeyValuePair<int, int[]> thisAltPath in copyAltRoutes)
                    {
                        int[] pathDetails = thisAltPath.Value;
                        pathDetails[0] = newIndices[pathDetails[0]];
                        AlternativeRoutes.Add(thisAltPath.Key, pathDetails);
                    }

                    // if remove, update indices in station xref

                    Dictionary<string, int[]> copyXRef = StationXRef;
                    StationXRef.Clear();

                    foreach (KeyValuePair<string, int[]> actXRef in copyXRef)
                    {
                        int[] oldValue = actXRef.Value;
                        int[] newValue = new int[3] { newIndices[oldValue[0]], oldValue[1], oldValue[2] };
                        StationXRef.Add(actXRef.Key, newValue);
                    }
                }

                // find if last stretch is dummy track

                // first, find last signal - there may not be a junction between last signal and end
                // last end must be end-of-track

                foreach (TCSubpathRoute endSubPath in TCRouteSubpaths)
                {
                    int lastIndex = endSubPath.Count - 1;
                    TCRouteElement thisElement = endSubPath[lastIndex];
                    TrackCircuitSection lastSection = orgSignals.TrackCircuitList[thisElement.TCSectionIndex];

                    // build additional route from end of last section but not further than train length

                    int nextSectionIndex = lastSection.ActivePins[thisElement.OutPin[0], thisElement.OutPin[1]].Link;
                    int nextDirection = lastSection.ActivePins[thisElement.OutPin[0], thisElement.OutPin[1]].Direction;
                    int lastUseIndex = lastIndex - 1;  // do not use final element if this is end of track

                    List<int> addSections = new List<int>();

                    if (nextSectionIndex > 0)
                    {
                        lastUseIndex = lastIndex;  // last element is not end of track
                        addSections = orgSignals.ScanRoute(null, nextSectionIndex, 0.0f, nextDirection,
                           true, thisTrainLength, false, true, true, false, true, false, false, false, false, false);

                        if (addSections.Count > 0)
                        {
                            lastSection = orgSignals.TrackCircuitList[Math.Abs(addSections[addSections.Count - 1])];
                        }
                    }

                    if (lastSection.CircuitType == TrackCircuitSection.TrackCircuitType.EndOfTrack)
                    {

                        // first length of added sections

                        float totalLength = 0.0f;
                        bool juncfound = false;

                        for (int iSection = 0; iSection < addSections.Count - 2; iSection++)  // exclude end of track
                        {
                            TrackCircuitSection thisSection = orgSignals.TrackCircuitList[Math.Abs(addSections[iSection])];
                            totalLength += thisSection.Length;
                            if (thisSection.CircuitType != TrackCircuitSection.TrackCircuitType.Normal)
                            {
                                juncfound = true;
                            }
                        }

                        // next length of sections back to last signal
                        // stop loop : when junction found, when signal found, when length exceeds train length

                        int sigIndex = -1;

                        for (int iSection = lastUseIndex;
                                iSection >= 0 && sigIndex < 0 && !juncfound && totalLength < 0.5 * thisTrainLength;
                                iSection--)
                        {
                            thisElement = endSubPath[iSection];
                            TrackCircuitSection thisSection = orgSignals.TrackCircuitList[thisElement.TCSectionIndex];

                            if (thisSection.EndSignals[thisElement.Direction] != null)
                            {
                                sigIndex = iSection;
                            }
                            else if (thisSection.CircuitType != TrackCircuitSection.TrackCircuitType.Normal)
                            {
                                juncfound = true;
                            }
                            else
                            {
                                totalLength += thisSection.Length;
                            }
                        }

                        // remove dummy ends

                        if (sigIndex > 0 && totalLength < 0.5f * thisTrainLength)
                        {
                            for (int iSection = endSubPath.Count - 1; iSection > sigIndex; iSection--)
                            {
                                if (endSubPath == TCRouteSubpaths[TCRouteSubpaths.Count - 1] &&
                                    endSubPath[iSection].TCSectionIndex == RoughReversalInfos[RoughReversalInfos.Count - 1].ReversalSectionIndex)
                                {
                                    RoughReversalInfos[RoughReversalInfos.Count - 1].ReversalSectionIndex = endSubPath[sigIndex].TCSectionIndex;
                                    RoughReversalInfos[RoughReversalInfos.Count - 1].ReverseReversalOffset = 
                                        orgSignals.TrackCircuitList[endSubPath[sigIndex].TCSectionIndex].Length;
                                }
                                endSubPath.RemoveAt(iSection);
                            }
                        }
                    }
                }

                // for reversals, find actual diverging section

                int prevDivergeSectorIndex = -1;
                int iReversalLists = 0;
                TCReversalInfo reversalInfo;
                for (int iSubpath = 1; iSubpath < TCRouteSubpaths.Count; iSubpath++)
                {
                    while (RoughReversalInfos.Count > 0 && RoughReversalInfos[iReversalLists].SubPathIndex < iSubpath - 1 && iReversalLists < RoughReversalInfos.Count - 2)
                    {
                        iReversalLists++;
                    }

                    if (RoughReversalInfos.Count > 0 && RoughReversalInfos[iReversalLists].SubPathIndex == iSubpath - 1)
                    {
                        reversalInfo = new TCReversalInfo(TCRouteSubpaths[iSubpath - 1], prevDivergeSectorIndex,
                            TCRouteSubpaths[iSubpath], orgSignals,
                            RoughReversalInfos[iReversalLists].ReverseReversalOffset, RoughReversalInfos[iReversalLists].SubPathIndex, RoughReversalInfos[iReversalLists].ReversalSectionIndex);
                    }
                    else
                    {
                        reversalInfo = new TCReversalInfo(TCRouteSubpaths[iSubpath - 1], prevDivergeSectorIndex,
                            TCRouteSubpaths[iSubpath], orgSignals, -1, -1, -1);
                    }

                    ReversalInfo.Add(reversalInfo);
                    prevDivergeSectorIndex = reversalInfo.Valid ? reversalInfo.FirstDivergeIndex : -1;
                }
                ReversalInfo.Add(new TCReversalInfo());  // add invalid item to make up the numbers (equals no. subpaths)
                // Insert data for end route offset
                ReversalInfo[ReversalInfo.Count - 1].ReverseReversalOffset = RoughReversalInfos[RoughReversalInfos.Count - 1].ReverseReversalOffset;
                ReversalInfo[ReversalInfo.Count - 1].ReversalIndex = RoughReversalInfos[RoughReversalInfos.Count - 1].SubPathIndex;
                ReversalInfo[ReversalInfo.Count - 1].ReversalSectionIndex = RoughReversalInfos[RoughReversalInfos.Count - 1].ReversalSectionIndex;

                RoughReversalInfos.Clear(); // no more used


                // process alternative paths - MSTS style

                if (orgSignals.UseLocationPassingPaths)
                {
                    ProcessAlternativePath_LocationDef(AlternativeRoutes, aiPath, orgSignals, trainNumber);
                    if (trainNumber >= 0) SearchPassingPaths(trainNumber, thisTrainLength, orgSignals);
                }
                else
                {
                    ProcessAlternativePath_PathDef(AlternativeRoutes, aiPath, orgSignals);
                }

                // search for loops

                LoopSearch(orgSignals);

#if DEBUG_TEST
                for (int iSub = 0; iSub < TCRouteSubpaths.Count; iSub++)
                {
                    TCSubpathRoute printSubpath = TCRouteSubpaths[iSub];
                    File.AppendAllText(@"C:\temp\TCSections.txt", "\n-- Subpath : " + iSub.ToString() + " --\n\n");

                    foreach (TCRouteElement printElement in printSubpath)
                    {
                        File.AppendAllText(@"C:\temp\TCSections.txt", " TC Index   : " + printElement.TCSectionIndex.ToString() + "\n");
                        File.AppendAllText(@"C:\temp\TCSections.txt", " direction  : " + printElement.Direction.ToString() + "\n");
                        File.AppendAllText(@"C:\temp\TCSections.txt",
                            " outpins    : " + printElement.OutPin[0].ToString() + " - " + printElement.OutPin[1].ToString() + "\n");
                        if (printElement.StartAlternativePath != null)
                        {
                            File.AppendAllText(@"C:\temp\TCSections.txt", "\n Start Alternative Path : " +
                        printElement.StartAlternativePath[0].ToString() +
                        " upto section " + printElement.StartAlternativePath[1].ToString() + "\n");
                        }
                        if (printElement.EndAlternativePath != null)
                        {
                            File.AppendAllText(@"C:\temp\TCSections.txt", "\n End Alternative Path : " +
                        printElement.EndAlternativePath[0].ToString() +
                        " from section " + printElement.EndAlternativePath[1].ToString() + "\n");
                        }

                        File.AppendAllText(@"C:\temp\TCSections.txt", "\n");
                    }

                    if (iSub < TCRouteSubpaths.Count - 1)
                    {
                        if (LoopEnd[iSub] > 0)
                        {
                            int loopSection = printSubpath[LoopEnd[iSub]].TCSectionIndex;
                            File.AppendAllText(@"C:\temp\TCSections.txt", "\n-- Loop at : " + LoopEnd[iSub] + " : section : " + loopSection + "--\n");
                        }
                        else if (ReversalInfo[iSub].Valid)
                        {
                            File.AppendAllText(@"C:\temp\TCSections.txt", "\n-- reversal --\n");
                        }
                        else
                        {
                            File.AppendAllText(@"C:\temp\TCSections.txt", "\n-- path break --\n");
                        }
                    }
                }

                for (int iAlt = 0; iAlt < TCAlternativePaths.Count; iAlt++)
                {
                    File.AppendAllText(@"C:\temp\TCSections.txt", "--------------------------------------------------\n");

                    TCSubpathRoute printSubpath = TCAlternativePaths[iAlt];
                    File.AppendAllText(@"C:\temp\TCSections.txt", "\n-- Alternative path : " + iAlt.ToString() + " --\n\n");

                    foreach (TCRouteElement printElement in printSubpath)
                    {
                        File.AppendAllText(@"C:\temp\TCSections.txt", " TC Index   : " + printElement.TCSectionIndex.ToString() + "\n");
                        File.AppendAllText(@"C:\temp\TCSections.txt", " direction  : " + printElement.Direction.ToString() + "\n");
                        File.AppendAllText(@"C:\temp\TCSections.txt",
                            " outpins    : " + printElement.OutPin[0].ToString() + " - " + printElement.OutPin[1].ToString() + "\n");
                        File.AppendAllText(@"C:\temp\TCSections.txt", "\n");
                    }
                }

                for (int iRI = 0; iRI < ReversalInfo.Count; iRI++)
                {
                    File.AppendAllText(@"C:\temp\TCSections.txt", "--------------------------------------------------\n");
                    File.AppendAllText(@"C:\temp\TCSections.txt", "\n-- Reversal Info : " + iRI.ToString() + " --\n\n");
                    TCReversalInfo thisReversalInfo = ReversalInfo[iRI];

                    File.AppendAllText(@"C:\temp\TCSections.txt", "Diverge sector : " + thisReversalInfo.DivergeSectorIndex.ToString() + "\n");
                    File.AppendAllText(@"C:\temp\TCSections.txt", "Diverge offset : " + thisReversalInfo.DivergeOffset.ToString() + "\n");
                    File.AppendAllText(@"C:\temp\TCSections.txt", "First Index    : " + thisReversalInfo.FirstDivergeIndex.ToString() + "\n");
                    File.AppendAllText(@"C:\temp\TCSections.txt", "First Signal   : " + thisReversalInfo.FirstSignalIndex.ToString() + "\n");
                    File.AppendAllText(@"C:\temp\TCSections.txt", "Last Index     : " + thisReversalInfo.LastDivergeIndex.ToString() + "\n");
                    File.AppendAllText(@"C:\temp\TCSections.txt", "Last Signal    : " + thisReversalInfo.LastSignalIndex.ToString() + "\n");
                }

                for (int iWP = 0; iWP < WaitingPoints.Count; iWP++)
                {

                    File.AppendAllText(@"C:\temp\TCSections.txt", "--------------------------------------------------\n");
                    File.AppendAllText(@"C:\temp\TCSections.txt", "\n-- Waiting Point Info : " + iWP.ToString() + " --\n\n");
                    int[] thisWaitingPoint = WaitingPoints[iWP];

                    File.AppendAllText(@"C:\temp\TCSections.txt", "Sublist   : " + thisWaitingPoint[0].ToString() + "\n");
                    File.AppendAllText(@"C:\temp\TCSections.txt", "Section   : " + thisWaitingPoint[1].ToString() + "\n");
                    File.AppendAllText(@"C:\temp\TCSections.txt", "Wait time : " + thisWaitingPoint[2].ToString() + "\n");
                    File.AppendAllText(@"C:\temp\TCSections.txt", "Dep time  : " + thisWaitingPoint[3].ToString() + "\n");
                }

                File.AppendAllText(@"C:\temp\TCSections.txt", "--------------------------------------------------\n");
#endif
            }

            //  SPA: Used with enhanced MSTS Mode, please don't change
            float GetOffsetToPathNode(AIPath aiPath, int direction, AIPathNode pathNode)
            {

                float offset = 0;
                TrackNode WPNode;
                TrVectorSection firstSection;
                //int nextNodeIdx = 0;
                int NodeDir = direction;

                WPNode = aiPath.TrackDB.TrackNodes[pathNode.NextMainTVNIndex];
                int idxSectionWP = ConvertWaitingPoint(pathNode, aiPath.TrackDB, aiPath.TSectionDat, direction);
                firstSection = WPNode.TrVectorNode.TrVectorSections[0];
                Traveller TDBTrav = new Traveller(aiPath.TSectionDat, aiPath.TrackDB.TrackNodes, WPNode,
                    firstSection.TileX, firstSection.TileZ,
                    firstSection.X, firstSection.Z, (Traveller.TravellerDirection)NodeDir);
                if (TDBTrav.Direction == Traveller.TravellerDirection.Backward)
                {
                    NodeDir = 1 - direction;
                    TDBTrav = new Traveller(aiPath.TSectionDat, aiPath.TrackDB.TrackNodes, WPNode,
                    firstSection.TileX, firstSection.TileZ,
                    firstSection.X, firstSection.Z, (Traveller.TravellerDirection)NodeDir);
                    offset = TDBTrav.DistanceTo(WPNode,
                        pathNode.Location.TileX, pathNode.Location.TileZ,
                        pathNode.Location.Location.X,
                        pathNode.Location.Location.Y,
                        pathNode.Location.Location.Z);
                    for (int idx = 0; idx < WPNode.TCCrossReference.Count(); idx++)
                    {
                        int TCSectionIndex = WPNode.TCCrossReference[idx].Index;
                        if (TCSectionIndex == idxSectionWP)
                        {
                            float sectionOffset = offset - WPNode.TCCrossReference[idx].OffsetLength[NodeDir];
                            offset = WPNode.TCCrossReference[idx].Length - sectionOffset;
                            break;
                        }
                    }
                }
                else
                {
                    //Trace.TraceInformation("no reverse");
                    offset = TDBTrav.DistanceTo(WPNode,
                        pathNode.Location.TileX, pathNode.Location.TileZ,
                        pathNode.Location.Location.X,
                        pathNode.Location.Location.Y,
                        pathNode.Location.Location.Z);
                    for (int idx = 0; idx < WPNode.TCCrossReference.Count(); idx++)
                    {
                        int TCSectionIndex = WPNode.TCCrossReference[idx].Index;
                        if (TCSectionIndex == idxSectionWP)
                        {
                            offset = offset - WPNode.TCCrossReference[idx].OffsetLength[NodeDir];
                            break;
                        }
                    }
                }
                return offset;
            }

            public String[] GetTCRouteInfo(String[] stateString, TCPosition position)
            {
                String[] retString = new String[stateString.Length];
                stateString.CopyTo(retString, 0);
                string TCSidxString = "Index : ";
                string lenTCcurrent = " ";
                int show = 0;
                string wpString = "";
                int[] tabWP = new int[WaitingPoints.Count];
                int cntWP = 0;
                foreach (var wp in WaitingPoints)
                {
                    if (wp[0] == activeSubpath)
                    {
                        tabWP[cntWP] = wp[1];
                    }
                    else
                    {
                        tabWP[cntWP] = 0;
                    }
                    cntWP++;
                }
                cntWP = 0;
                TCSidxString = String.Concat(TCSidxString, "(", activeSubpath.ToString(), "):");
                foreach (var subpath in TCRouteSubpaths[activeSubpath])
                {
                    if (position.TCSectionIndex != subpath.TCSectionIndex)
                        show++;
                    if (position.TCSectionIndex == subpath.TCSectionIndex)
                        break;
                }
                int cnt = 0;
                foreach (var subpath in TCRouteSubpaths[activeSubpath])
                {
                    if (tabWP.Count() > 0 && subpath.TCSectionIndex == tabWP[activeSubpath])
                    {
                        wpString = String.Concat("(wp:", WaitingPoints[activeSubpath][2].ToString(), "sec)");
                        tabWP[activeSubpath] = 0;
                    }
                    else
                    {
                        wpString = "";
                    }
                    if (position.TCSectionIndex == subpath.TCSectionIndex)
                    {
                        lenTCcurrent = String.Concat(" (", position.DistanceTravelledM.ToString("F0"), ")");
                        TCSidxString = String.Concat(TCSidxString, subpath.TCSectionIndex.ToString(), lenTCcurrent, wpString, ", ");
                    }
                    else if (cnt > show - 3 && cnt < show)
                    {
                        TCSidxString = String.Concat(TCSidxString, "{", subpath.TCSectionIndex.ToString(), "}", wpString, ", ");
                    }
                    else if (cnt > show && cnt < show + 10)
                    {
                        TCSidxString = String.Concat(TCSidxString, subpath.TCSectionIndex.ToString(), wpString, ", ");
                        lenTCcurrent = "";
                    }
                    cnt++;
                    if (cnt > show + 10)
                        break;
                }
                retString[3] = "...";
                retString[4] = TCSidxString;
                return (retString);

            }
            //================================================================================================//
            //
            // process alternative paths - MSTS style Path definition
            //

            public void ProcessAlternativePath_PathDef(Dictionary<int, int[]> AlternativeRoutes, AIPath aiPath, Signals orgSignals)
            {
                int altlist = 0;

                foreach (KeyValuePair<int, int[]> thisAltPath in AlternativeRoutes)
                {
                    TCSubpathRoute thisAltpath = new TCSubpathRoute();

                    int startSection = thisAltPath.Key;
                    int[] pathDetails = thisAltPath.Value;
                    int sublistRef = pathDetails[0];

                    int startSectionRouteIndex = TCRouteSubpaths[sublistRef].GetRouteIndex(startSection, 0);
                    int endSectionRouteIndex = -1;

                    int endSection = pathDetails[2];
                    if (endSection < 0)
                    {
                        Trace.TraceInformation("No end-index found for alternative path starting at " + startSection.ToString());
                    }
                    else
                    {
                        endSectionRouteIndex = TCRouteSubpaths[sublistRef].GetRouteIndex(endSection, 0);
                    }

                    if (startSectionRouteIndex < 0 || endSectionRouteIndex < 0)
                    {
                        Trace.TraceInformation("Start section " + startSection.ToString() + "or end section " + endSection.ToString() +
                                               " for alternative path not in subroute " + sublistRef.ToString());
                    }
                    else
                    {
                        TCRouteElement startElement = TCRouteSubpaths[sublistRef][startSectionRouteIndex];
                        TCRouteElement endElement = TCRouteSubpaths[sublistRef][endSectionRouteIndex];

                        startElement.StartAlternativePath = new int[2];
                        startElement.StartAlternativePath[0] = altlist;
                        startElement.StartAlternativePath[1] = endSection;

                        endElement.EndAlternativePath = new int[2];
                        endElement.EndAlternativePath[0] = altlist;
                        endElement.EndAlternativePath[1] = startSection;

                        int currentDir = startElement.Direction;
                        int newDir = currentDir;

                        //
                        // loop through path nodes
                        //

                        AIPathNode thisPathNode = aiPath.Nodes[pathDetails[1]];
                        AIPathNode nextPathNode = null;
                        AIPathNode lastPathNode = null;

                        // process junction node

                        TrackNode firstJunctionNode = aiPath.TrackDB.TrackNodes[thisPathNode.JunctionIndex];
                        TCRouteElement thisJunctionElement =
                            new TCRouteElement(firstJunctionNode, 0, currentDir, orgSignals);
                        thisAltpath.Add(thisJunctionElement);

                        int trackNodeIndex = thisPathNode.NextSidingTVNIndex;

                        uint firstJunctionPin = (firstJunctionNode.Inpins > 1) ? 0 : firstJunctionNode.Inpins;
                        if (firstJunctionNode.TrPins[firstJunctionPin].Link == trackNodeIndex)
                        {
                            currentDir = firstJunctionNode.TrPins[firstJunctionPin].Direction;
                            thisJunctionElement.OutPin[1] = 0;
                        }
                        else
                        {
                            firstJunctionPin++;
                            currentDir = firstJunctionNode.TrPins[firstJunctionPin].Direction;
                            thisJunctionElement.OutPin[1] = 1;
                        }

                        // process alternative path

                        TrackNode thisNode = null;
                        thisPathNode = thisPathNode.NextSidingNode;

                        while (thisPathNode != null)
                        {

                            //
                            // process last non-junction section
                            //

                            if (thisPathNode.Type == AIPathNodeType.Other)
                            {
                                if (trackNodeIndex > 0)
                                {
                                    thisNode = aiPath.TrackDB.TrackNodes[trackNodeIndex];

                                    if (currentDir == 0)
                                    {
                                        for (int iTC = 0; iTC < thisNode.TCCrossReference.Count; iTC++)
                                        {
                                            TCRouteElement thisElement =
                                                new TCRouteElement(thisNode, iTC, currentDir, orgSignals);
                                            thisAltpath.Add(thisElement);
                                        }
                                        newDir = thisNode.TrPins[currentDir].Direction;

                                    }
                                    else
                                    {
                                        for (int iTC = thisNode.TCCrossReference.Count - 1; iTC >= 0; iTC--)
                                        {
                                            TCRouteElement thisElement =
                                                new TCRouteElement(thisNode, iTC, currentDir, orgSignals);
                                            thisAltpath.Add(thisElement);
                                        }
                                        newDir = thisNode.TrPins[currentDir].Direction;
                                    }
                                    trackNodeIndex = -1;
                                }

                                //
                                // process junction section
                                //

                                if (thisPathNode.JunctionIndex > 0)
                                {
                                    TrackNode junctionNode = aiPath.TrackDB.TrackNodes[thisPathNode.JunctionIndex];
                                    TCRouteElement thisElement =
                                        new TCRouteElement(junctionNode, 0, newDir, orgSignals);
                                    thisAltpath.Add(thisElement);

                                    trackNodeIndex = thisPathNode.NextSidingTVNIndex;

                                    if (thisPathNode.IsFacingPoint)   // exit is one of two switch paths //
                                    {
                                        uint firstpin = (junctionNode.Inpins > 1) ? 0 : junctionNode.Inpins;
                                        if (junctionNode.TrPins[firstpin].Link == trackNodeIndex)
                                        {
                                            newDir = junctionNode.TrPins[firstpin].Direction;
                                            thisElement.OutPin[1] = 0;
                                        }
                                        else
                                        {
                                            firstpin++;
                                            newDir = junctionNode.TrPins[firstpin].Direction;
                                            thisElement.OutPin[1] = 1;
                                        }
                                    }
                                    else  // exit is single path //
                                    {
                                        uint firstpin = (junctionNode.Inpins > 1) ? junctionNode.Inpins : 0;
                                        newDir = junctionNode.TrPins[firstpin].Direction;
                                    }
                                }

                                currentDir = newDir;

                                //
                                // find next junction path node
                                //

                                nextPathNode = thisPathNode.NextSidingNode;
                            }
                            else
                            {
                                nextPathNode = thisPathNode;
                            }

                            while (nextPathNode != null && nextPathNode.JunctionIndex < 0)
                            {
                                nextPathNode = nextPathNode.NextSidingNode;
                            }

                            lastPathNode = thisPathNode;
                            thisPathNode = nextPathNode;
                        }
                        //
                        // add last section
                        //

                        if (trackNodeIndex > 0)
                        {
                            thisNode = aiPath.TrackDB.TrackNodes[trackNodeIndex];

                            if (currentDir == 0)
                            {
                                for (int iTC = 0; iTC < thisNode.TCCrossReference.Count; iTC++)
                                {
                                    TCRouteElement thisElement =
                                        new TCRouteElement(thisNode, iTC, currentDir, orgSignals);
                                    thisAltpath.Add(thisElement);
                                }
                            }
                            else
                            {
                                for (int iTC = thisNode.TCCrossReference.Count - 1; iTC >= 0; iTC--)
                                {
                                    TCRouteElement thisElement =
                                        new TCRouteElement(thisNode, iTC, currentDir, orgSignals);
                                    thisAltpath.Add(thisElement);
                                }
                            }
                        }

                        TCAlternativePaths.Add(thisAltpath);
                        altlist++;
                    }
                }
            }

            //================================================================================================//
            //
            // process alternative paths - location definition
            //

            public void ProcessAlternativePath_LocationDef(Dictionary<int, int[]> AlternativeRoutes, AIPath aiPath, Signals orgSignals, int trainNumber)
            {
                foreach (KeyValuePair<int, int[]> thisAltPathIndex in AlternativeRoutes)
                {
                    TCSubpathRoute thisAltpath = new TCSubpathRoute();

                    int startSectionIndex = thisAltPathIndex.Key;
                    int[] pathDetails = thisAltPathIndex.Value;
                    int sublistRef = pathDetails[0];

                    int startSectionRouteIndex = TCRouteSubpaths[sublistRef].GetRouteIndex(startSectionIndex, 0);
                    int endSectionRouteIndex = -1;

                    int endSectionIndex = pathDetails[2];
                    if (endSectionIndex < 0)
                    {
                        Trace.TraceInformation("No end-index found for passing path for train {0} starting at {1}",
                            trainNumber, startSectionIndex.ToString());
                    }
                    else
                    {
                        endSectionRouteIndex = TCRouteSubpaths[sublistRef].GetRouteIndex(endSectionIndex, 0);
                    }

                    if (startSectionRouteIndex < 0 || endSectionRouteIndex < 0)
                    {
                        Trace.TraceInformation("Start section " + startSectionIndex.ToString() + "or end section " + endSectionIndex.ToString() +
                                               " for passing path not in subroute " + sublistRef.ToString());
                    }
                    else
                    {
                        TCRouteElement startElement = TCRouteSubpaths[sublistRef][startSectionRouteIndex];
                        int currentDir = startElement.Direction;
                        int newDir = currentDir;

                        //
                        // loop through path nodes
                        //

                        AIPathNode thisPathNode = aiPath.Nodes[pathDetails[1]];
                        AIPathNode nextPathNode = null;
                        AIPathNode lastPathNode = null;

                        // process junction node

                        TrackNode firstJunctionNode = aiPath.TrackDB.TrackNodes[thisPathNode.JunctionIndex];
                        TCRouteElement thisJunctionElement =
                            new TCRouteElement(firstJunctionNode, 0, currentDir, orgSignals);
                        thisAltpath.Add(thisJunctionElement);

                        int trackNodeIndex = thisPathNode.NextSidingTVNIndex;

                        uint firstJunctionPin = (firstJunctionNode.Inpins > 1) ? 0 : firstJunctionNode.Inpins;
                        if (firstJunctionNode.TrPins[firstJunctionPin].Link == trackNodeIndex)
                        {
                            currentDir = firstJunctionNode.TrPins[firstJunctionPin].Direction;
                            thisJunctionElement.OutPin[1] = 0;
                        }
                        else
                        {
                            firstJunctionPin++;
                            currentDir = firstJunctionNode.TrPins[firstJunctionPin].Direction;
                            thisJunctionElement.OutPin[1] = 1;
                        }

                        // process alternative path

                        TrackNode thisNode = null;
                        thisPathNode = thisPathNode.NextSidingNode;

                        while (thisPathNode != null)
                        {

                            //
                            // process last non-junction section
                            //

                            if (thisPathNode.Type == AIPathNodeType.Other)
                            {
                                if (trackNodeIndex > 0)
                                {
                                    thisNode = aiPath.TrackDB.TrackNodes[trackNodeIndex];

                                    if (currentDir == 0)
                                    {
                                        for (int iTC = 0; iTC < thisNode.TCCrossReference.Count; iTC++)
                                        {
                                            TCRouteElement thisElement =
                                                new TCRouteElement(thisNode, iTC, currentDir, orgSignals);
                                            thisAltpath.Add(thisElement);
                                        }
                                        newDir = thisNode.TrPins[currentDir].Direction;

                                    }
                                    else
                                    {
                                        for (int iTC = thisNode.TCCrossReference.Count - 1; iTC >= 0; iTC--)
                                        {
                                            TCRouteElement thisElement =
                                                new TCRouteElement(thisNode, iTC, currentDir, orgSignals);
                                            thisAltpath.Add(thisElement);
                                        }
                                        newDir = thisNode.TrPins[currentDir].Direction;
                                    }
                                    trackNodeIndex = -1;
                                }

                                //
                                // process junction section
                                //

                                if (thisPathNode.JunctionIndex > 0)
                                {
                                    TrackNode junctionNode = aiPath.TrackDB.TrackNodes[thisPathNode.JunctionIndex];
                                    TCRouteElement thisElement =
                                        new TCRouteElement(junctionNode, 0, newDir, orgSignals);
                                    thisAltpath.Add(thisElement);

                                    trackNodeIndex = thisPathNode.NextSidingTVNIndex;

                                    if (thisPathNode.IsFacingPoint)   // exit is one of two switch paths //
                                    {
                                        uint firstpin = (junctionNode.Inpins > 1) ? 0 : junctionNode.Inpins;
                                        if (junctionNode.TrPins[firstpin].Link == trackNodeIndex)
                                        {
                                            newDir = junctionNode.TrPins[firstpin].Direction;
                                            thisElement.OutPin[1] = 0;
                                        }
                                        else
                                        {
                                            firstpin++;
                                            newDir = junctionNode.TrPins[firstpin].Direction;
                                            thisElement.OutPin[1] = 1;
                                        }
                                    }
                                    else  // exit is single path //
                                    {
                                        uint firstpin = (junctionNode.Inpins > 1) ? junctionNode.Inpins : 0;
                                        newDir = junctionNode.TrPins[firstpin].Direction;
                                    }
                                }

                                currentDir = newDir;

                                //
                                // find next junction path node
                                //

                                nextPathNode = thisPathNode.NextSidingNode;
                            }
                            else
                            {
                                nextPathNode = thisPathNode;
                            }

                            while (nextPathNode != null && nextPathNode.JunctionIndex < 0)
                            {
                                nextPathNode = nextPathNode.NextSidingNode;
                            }

                            lastPathNode = thisPathNode;
                            thisPathNode = nextPathNode;
                        }
                        //
                        // add last section
                        //

                        if (trackNodeIndex > 0)
                        {
                            thisNode = aiPath.TrackDB.TrackNodes[trackNodeIndex];

                            if (currentDir == 0)
                            {
                                for (int iTC = 0; iTC < thisNode.TCCrossReference.Count; iTC++)
                                {
                                    TCRouteElement thisElement =
                                        new TCRouteElement(thisNode, iTC, currentDir, orgSignals);
                                    thisAltpath.Add(thisElement);
                                }
                            }
                            else
                            {
                                for (int iTC = thisNode.TCCrossReference.Count - 1; iTC >= 0; iTC--)
                                {
                                    TCRouteElement thisElement =
                                        new TCRouteElement(thisNode, iTC, currentDir, orgSignals);
                                    thisAltpath.Add(thisElement);
                                }
                            }
                        }

                        InsertPassingPath(TCRouteSubpaths[sublistRef], thisAltpath, startSectionIndex, endSectionIndex, orgSignals, trainNumber, sublistRef);
                    }
                }
            }
            // check if path is valid diverge path

            //================================================================================================//
            //
            // process alternative paths - location definition
            // main path may be NULL if private path is to be set for fixed deadlocks
            //

            public void InsertPassingPath(TCSubpathRoute mainPath, TCSubpathRoute passPath, int startSectionIndex, int endSectionIndex,
                                  Signals orgSignals, int trainNumber, int sublistRef)
            {
                // if main set, check if path is valid diverge path - otherwise assume it is indeed

                if (mainPath != null)
                {
                    int[,] validPassingPath = mainPath.FindActualDivergePath(passPath, 0, mainPath.Count - 1);

                    if (validPassingPath[0, 0] < 0)
                    {
                        Trace.TraceInformation("Invalid passing path defined for train {0} at section {1} : path does not diverge from main path",
                            trainNumber, startSectionIndex);
                        return;
                    }
                }

                // find related deadlock definition - note that path may be extended to match other deadlock paths

                DeadlockInfo thisDeadlock = new DeadlockInfo(orgSignals);
                thisDeadlock = thisDeadlock.FindDeadlockInfo(ref passPath, mainPath, startSectionIndex, endSectionIndex);

                if (thisDeadlock == null) // path is not valid in relation to other deadlocks
                {
                    Trace.TraceInformation("Invalid passing path defined for train {0} at section {1} : overlaps with other passing path", trainNumber, startSectionIndex);
                    return;
                }

                // insert main path

                int usedStartSectionIndex = passPath[0].TCSectionIndex;
                int usedEndSectionIndex = passPath[passPath.Count - 1].TCSectionIndex;
                int usedStartSectionRouteIndex = mainPath.GetRouteIndex(usedStartSectionIndex, 0);
                int usedEndSectionRouteIndex = mainPath.GetRouteIndex(usedEndSectionIndex, usedStartSectionRouteIndex);

                TCSubpathRoute mainPathPart = new TCSubpathRoute(mainPath, usedStartSectionRouteIndex, usedEndSectionRouteIndex);
                if (mainPathPart != null)
                {
                    int[] mainIndex = thisDeadlock.AddPath(mainPathPart, usedStartSectionIndex);  // [0] is Index, [1] > 0 is existing

                    if (mainIndex[1] < 0)
                    {
                        // calculate usefull lenght and usefull end section for main path
                        Dictionary<int, float> mainPathUsefullInfo = mainPathPart.GetUsefullLength(0.0f, orgSignals, -1, -1);
                        KeyValuePair<int, float> mainPathUsefullValues = mainPathUsefullInfo.ElementAt(0);

                        DeadlockPathInfo thisDeadlockPathInfo = thisDeadlock.AvailablePathList[mainIndex[0]];
                        thisDeadlockPathInfo.EndSectionIndex = usedEndSectionIndex;
                        thisDeadlockPathInfo.LastUsefullSectionIndex = mainPathUsefullValues.Key;
                        thisDeadlockPathInfo.UsefullLength = mainPathUsefullValues.Value;

                        // only allow as public path if not in timetable mode
                        if (orgSignals.Simulator.TimetableMode)
                        {
                            thisDeadlockPathInfo.AllowedTrains.Add(thisDeadlock.GetTrainAndSubpathIndex(trainNumber, sublistRef));
                        }
                        else
                        {
                            thisDeadlockPathInfo.AllowedTrains.Add(-1); // set as public path
                        }

                        // if name is main insert inverse path also as MAIN to ensure reverse path is available

                        if (String.Compare(thisDeadlockPathInfo.Name, "MAIN") == 0 && !orgSignals.Simulator.TimetableMode)
                        {
                            TCSubpathRoute inverseMainPath = mainPathPart.ReversePath(orgSignals);
                            int[] inverseIndex = thisDeadlock.AddPath(inverseMainPath, endSectionIndex, "MAIN", String.Empty);
                            DeadlockPathInfo thisDeadlockInverseInfo = thisDeadlock.AvailablePathList[inverseIndex[0]];

                            Dictionary<int, float> mainInversePathUsefullInfo = inverseMainPath.GetUsefullLength(0.0f, orgSignals, -1, -1);
                            KeyValuePair<int, float> mainInversePathUsefullValues = mainInversePathUsefullInfo.ElementAt(0);

                            thisDeadlockInverseInfo.EndSectionIndex = startSectionIndex;
                            thisDeadlockInverseInfo.LastUsefullSectionIndex = mainInversePathUsefullValues.Key;
                            thisDeadlockInverseInfo.UsefullLength = mainInversePathUsefullValues.Value;
                            thisDeadlockInverseInfo.AllowedTrains.Add(-1);
                        }
                    }
                    // if existing path, add trainnumber if set and path is not public
                    else if (trainNumber >= 0)
                    {
                        DeadlockPathInfo thisDeadlockPathInfo = thisDeadlock.AvailablePathList[mainIndex[0]];
                        if (!thisDeadlockPathInfo.AllowedTrains.Contains(-1))
                        {
                            thisDeadlockPathInfo.AllowedTrains.Add(thisDeadlock.GetTrainAndSubpathIndex(trainNumber, sublistRef));
                        }
                    }
                }

                // add passing path

                int[] passIndex = thisDeadlock.AddPath(passPath, startSectionIndex);

                if (passIndex[1] < 0)
                {
                    // calculate usefull lenght and usefull end section for passing path
                    Dictionary<int, float> altPathUsefullInfo = passPath.GetUsefullLength(0.0f, orgSignals, -1, -1);
                    KeyValuePair<int, float> altPathUsefullValues = altPathUsefullInfo.ElementAt(0);

                    DeadlockPathInfo thisDeadlockPathInfo = thisDeadlock.AvailablePathList[passIndex[0]];
                    thisDeadlockPathInfo.EndSectionIndex = endSectionIndex;
                    thisDeadlockPathInfo.LastUsefullSectionIndex = altPathUsefullValues.Key;
                    thisDeadlockPathInfo.UsefullLength = altPathUsefullValues.Value;

                    if (trainNumber > 0)
                    {
                        thisDeadlockPathInfo.AllowedTrains.Add(thisDeadlock.GetTrainAndSubpathIndex(trainNumber, sublistRef));
                    }
                    else
                    {
                        thisDeadlockPathInfo.AllowedTrains.Add(-1);
                    }

                    // insert inverse path only if public

                    if (trainNumber < 0)
                    {
                        TCSubpathRoute inversePassPath = passPath.ReversePath(orgSignals);
                        int[] inverseIndex =
                            thisDeadlock.AddPath(inversePassPath, endSectionIndex, String.Copy(thisDeadlockPathInfo.Name), String.Empty);
                        DeadlockPathInfo thisDeadlockInverseInfo = thisDeadlock.AvailablePathList[inverseIndex[0]];

                        Dictionary<int, float> altInversePathUsefullInfo = inversePassPath.GetUsefullLength(0.0f, orgSignals, -1, -1);
                        KeyValuePair<int, float> altInversePathUsefullValues = altInversePathUsefullInfo.ElementAt(0);

                        thisDeadlockInverseInfo.EndSectionIndex = startSectionIndex;
                        thisDeadlockInverseInfo.LastUsefullSectionIndex = altInversePathUsefullValues.Key;
                        thisDeadlockInverseInfo.UsefullLength = altInversePathUsefullValues.Value;
                        thisDeadlockInverseInfo.AllowedTrains.Add(-1);
                    }
                }
                // if existing path, add trainnumber if set and path is not public
                else if (trainNumber >= 0)
                {
                    DeadlockPathInfo thisDeadlockPathInfo = thisDeadlock.AvailablePathList[passIndex[0]];
                    if (!thisDeadlockPathInfo.AllowedTrains.Contains(-1))
                    {
                        thisDeadlockPathInfo.AllowedTrains.Add(thisDeadlock.GetTrainAndSubpathIndex(trainNumber, sublistRef));
                    }
                }
            }

            //================================================================================================//
            //
            // search for valid passing paths
            // includes public paths
            //

            public void SearchPassingPaths(int trainNumber, float trainLength, Signals orgSignals)
            {
                for (int iSubpath = 0; iSubpath <= TCRouteSubpaths.Count - 1; iSubpath++)
                {
                    TCSubpathRoute thisSubpath = TCRouteSubpaths[iSubpath];

                    for (int iElement = 0; iElement <= thisSubpath.Count - 1; iElement++)
                    {
                        TCRouteElement thisElement = thisSubpath[iElement];
                        TrackCircuitSection thisSection = orgSignals.TrackCircuitList[thisElement.TCSectionIndex];

                        // if section is a deadlock boundary determine available paths
                        if (thisSection.DeadlockReference > 0)
                        {
                            DeadlockInfo thisDeadlockInfo = orgSignals.DeadlockInfoList[thisSection.DeadlockReference];
                            int nextElement = thisDeadlockInfo.SetTrainDetails(trainNumber, iSubpath, trainLength, thisSubpath, iElement);

                            if (nextElement < 0) // end of path reached
                            {
                                break;
                            }
                            else // skip deadlock area
                            {
                                iElement = nextElement;
                            }
                        }
                    }
                }
            }

            //================================================================================================//
            //
            // search for loops
            //

            public void LoopSearch(Signals orgSignals)
            {
                List<List<int[]>> loopList = new List<List<int[]>>();

                foreach (TCSubpathRoute thisRoute in TCRouteSubpaths)
                {
                    Dictionary<int, int> sections = new Dictionary<int, int>();
                    List<int[]> loopInfo = new List<int[]>();
                    loopList.Add(loopInfo);

                    bool loopset = false;
                    bool loopreverse = false; // loop is reversing loop, otherwise loop is continuing loop
                    int loopindex = -1;

                    for (int iElement = 0; iElement < thisRoute.Count; iElement++)
                    {
                        TCRouteElement thisElement = thisRoute[iElement];
                        int sectionIndex = thisElement.TCSectionIndex;

                        if (sections.ContainsKey(thisElement.TCSectionIndex) && !loopset)
                        {
                            int[] loopDetails = new int[2];
                            loopindex = sections[thisElement.TCSectionIndex];
                            loopDetails[0] = loopindex;
                            loopDetails[1] = iElement;
                            loopInfo.Add(loopDetails);
                            loopset = true;

                            // check if loop reverses or continues
                            loopreverse = thisElement.Direction != thisRoute[loopindex].Direction;

                        }
                        else if (sections.ContainsKey(thisElement.TCSectionIndex) && loopset)
                        {
                            int preloopindex = sections[thisElement.TCSectionIndex];

                            if (thisElement.Direction == thisRoute[preloopindex].Direction)
                            {
                                loopindex++;
                            }
                            else
                            {
                                loopindex--;
                            }

                            if (loopindex >= 0 && loopindex <= (thisRoute.Count - 1))
                            {
                                loopset = (thisElement.TCSectionIndex == thisRoute[loopindex].TCSectionIndex);
                            }
                        }
                        else
                        {
                            loopset = false;
                        }

                        if (!loopset && !sections.ContainsKey(thisElement.TCSectionIndex))
                        {
                            sections.Add(thisElement.TCSectionIndex, iElement);
                        }
                    }
                }

                // check for inner loops within outer loops
                // if found, remove outer loops

                for (int iRoute = 0; iRoute <= TCRouteSubpaths.Count - 1; iRoute++)
                {
                    List<int> invalids = new List<int>();
                    for (int iLoop = loopList[iRoute].Count - 1; iLoop >= 1; iLoop--)
                    {
                        if (loopList[iRoute][iLoop][1] > loopList[iRoute][iLoop - 1][0] && loopList[iRoute][iLoop][0] < loopList[iRoute][iLoop - 1][1])
                        {
                            invalids.Add(iLoop);
                        }
                    }
                    foreach (int iLoopRemove in invalids)
                    {
                        loopList[iRoute].RemoveAt(iLoopRemove);
                    }
                }

                // preset loop ends to invalid

                for (int iRoute = 0; iRoute <= TCRouteSubpaths.Count - 1; iRoute++)
                {
                    LoopEnd.Add(-1);
                }

                // split loops with overlap - search backward as subroutes may be added

                int orgTotalRoutes = TCRouteSubpaths.Count;
                for (int iRoute = orgTotalRoutes - 1; iRoute >= 0; iRoute--)
                {
                    TCSubpathRoute thisRoute = TCRouteSubpaths[iRoute];

                    List<int[]> loopInfo = loopList[iRoute];

                    // loop through looppoints backward as well
                    for (int iLoop = loopInfo.Count - 1; iLoop >= 0; iLoop--)
                    {
                        int[] loopDetails = loopInfo[iLoop];

                        // copy route and add after existing route
                        // remove points from loop-end in first route
                        // remove points upto loop-start in second route
                        TCSubpathRoute newRoute = new TCSubpathRoute(thisRoute);
                        thisRoute.RemoveRange(loopDetails[1], thisRoute.Count - loopDetails[1]);
                        newRoute.RemoveRange(0, loopDetails[0] + 1);

                        // add new route to list
                        TCRouteSubpaths.Insert(iRoute + 1, newRoute);

                        // set loop end
                        LoopEnd.Insert(iRoute, thisRoute[loopDetails[0]].TCSectionIndex);

                        // create dummy reversal lists
                        // shift waiting points and reversal lists
                        TCReversalInfo dummyReversal = new TCReversalInfo();
                        dummyReversal.Valid = false;
                        dummyReversal.ReversalSectionIndex = thisRoute[thisRoute.Count - 1].TCSectionIndex;
                        dummyReversal.ReversalIndex = thisRoute.Count - 1;
                        TrackCircuitSection thisSection = orgSignals.TrackCircuitList[thisRoute[thisRoute.Count - 1].TCSectionIndex];
                        dummyReversal.ReverseReversalOffset = thisSection.Length;
                        ReversalInfo.Insert(iRoute, dummyReversal);

                        foreach (int[] thisWaitingPoint in WaitingPoints)
                        {
                            if (thisWaitingPoint[0] >= iRoute) thisWaitingPoint[0]++;
                        }
                    }
                }
            }

            //================================================================================================//
            //
            // Constructor from existing path
            //

            public TCRoutePath(TCRoutePath otherPath)
            {
                activeSubpath = otherPath.activeSubpath;
                activeAltpath = otherPath.activeAltpath;

                for (int iSubpath = 0; iSubpath < otherPath.TCRouteSubpaths.Count; iSubpath++)
                {
                    TCSubpathRoute newSubpath = new TCSubpathRoute(otherPath.TCRouteSubpaths[iSubpath]);
                    TCRouteSubpaths.Add(newSubpath);
                }

                for (int iAltpath = 0; iAltpath < otherPath.TCAlternativePaths.Count; iAltpath++)
                {
                    TCSubpathRoute newAltpath = new TCSubpathRoute(otherPath.TCAlternativePaths[iAltpath]);
                    TCAlternativePaths.Add(newAltpath);
                }

                for (int iWaitingPoint = 0; iWaitingPoint < otherPath.WaitingPoints.Count; iWaitingPoint++)
                {
                    int[] oldWaitingPoint = otherPath.WaitingPoints[iWaitingPoint];
                    int[] newWaitingPoint = new int[oldWaitingPoint.Length];
                    oldWaitingPoint.CopyTo(newWaitingPoint, 0);
                    WaitingPoints.Add(newWaitingPoint);
                }

                for (int iReversalPoint = 0; iReversalPoint < otherPath.ReversalInfo.Count; iReversalPoint++)
                {
                    if (otherPath.ReversalInfo[iReversalPoint] == null)
                    {
                        ReversalInfo.Add(null);
                    }
                    else
                    {
                        TCReversalInfo reversalInfo = new TCReversalInfo(otherPath.ReversalInfo[iReversalPoint]);
                        ReversalInfo.Add(reversalInfo);
                    }
                }

                for (int iLoopEnd = 0; iLoopEnd < otherPath.LoopEnd.Count; iLoopEnd++)
                {
                    LoopEnd.Add(otherPath.LoopEnd[iLoopEnd]);
                }

                foreach (KeyValuePair<string, int[]> actStation in otherPath.StationXRef)
                {
                    StationXRef.Add(actStation.Key, actStation.Value);
                }
            }

            //================================================================================================//
            //
            // Constructor from single subpath
            //

            public TCRoutePath(TCSubpathRoute subPath)
            {
                activeSubpath = 0;
                activeAltpath = -1;

                TCRouteSubpaths.Add(subPath);
            }

            //================================================================================================//
            //
            // Restore
            //

            public TCRoutePath(BinaryReader inf)
            {
                activeSubpath = inf.ReadInt32();
                activeAltpath = inf.ReadInt32();

                int totalSubpath = inf.ReadInt32();
                for (int iSubpath = 0; iSubpath < totalSubpath; iSubpath++)
                {
                    TCSubpathRoute thisSubpath = new TCSubpathRoute(inf);
                    TCRouteSubpaths.Add(thisSubpath);
                }

                int totalAltpath = inf.ReadInt32();
                for (int iAltpath = 0; iAltpath < totalAltpath; iAltpath++)
                {
                    TCSubpathRoute thisSubpath = new TCSubpathRoute(inf);
                    TCAlternativePaths.Add(thisSubpath);
                }

                int totalWaitingPoint = inf.ReadInt32();
                for (int iWP = 0; iWP < totalWaitingPoint; iWP++)
                {
                    int[] waitingPoint = new int[6];
                    waitingPoint[0] = inf.ReadInt32();
                    waitingPoint[1] = inf.ReadInt32();
                    waitingPoint[2] = inf.ReadInt32();
                    waitingPoint[3] = inf.ReadInt32();
                    waitingPoint[4] = inf.ReadInt32();
                    waitingPoint[5] = inf.ReadInt32();

                    WaitingPoints.Add(waitingPoint);
                }

                int totalReversalPoint = inf.ReadInt32();
                for (int iRP = 0; iRP < totalReversalPoint; iRP++)
                {
                    ReversalInfo.Add(new TCReversalInfo(inf));
                }

                int totalLoopEnd = inf.ReadInt32();
                for (int iLE = 0; iLE < totalLoopEnd; iLE++)
                {
                    LoopEnd.Add(inf.ReadInt32());
                }

                OriginalSubpath = inf.ReadInt32();

                // note : stationXRef only used on init, not saved
            }

            //================================================================================================//
            //
            // Save
            //

            public void Save(BinaryWriter outf)
            {
                outf.Write(activeSubpath);
                outf.Write(activeAltpath);
                outf.Write(TCRouteSubpaths.Count);
                foreach (TCSubpathRoute thisSubpath in TCRouteSubpaths)
                {
                    thisSubpath.Save(outf);
                }

                outf.Write(TCAlternativePaths.Count);
                foreach (TCSubpathRoute thisAltpath in TCAlternativePaths)
                {
                    thisAltpath.Save(outf);
                }

                outf.Write(WaitingPoints.Count);
                foreach (int[] waitingPoint in WaitingPoints)
                {
                    outf.Write(waitingPoint[0]);
                    outf.Write(waitingPoint[1]);
                    outf.Write(waitingPoint[2]);
                    outf.Write(waitingPoint[3]);
                    outf.Write(waitingPoint[4]);
                    outf.Write(waitingPoint[5]);
                }

                outf.Write(ReversalInfo.Count);
                for (int iRP = 0; iRP < ReversalInfo.Count; iRP++)
                {
                    ReversalInfo[iRP].Save(outf);
                }

                outf.Write(LoopEnd.Count);
                for (int iLE = 0; iLE < LoopEnd.Count; iLE++)
                {
                    outf.Write(LoopEnd[iLE]);
                }

                outf.Write(OriginalSubpath);

                // note : stationXRef only used on init, need not be saved
            }

            //================================================================================================//
            //
            // Convert waiting point to section no.
            //

            static int ConvertWaitingPoint(AIPathNode stopPathNode, TrackDB TrackDB, TrackSectionsFile TSectionDat, int direction)
            {
                TrackNode waitingNode = TrackDB.TrackNodes[stopPathNode.NextMainTVNIndex];
                TrVectorSection firstSection = waitingNode.TrVectorNode.TrVectorSections[0];
                Traveller TDBTrav = new Traveller(TSectionDat, TrackDB.TrackNodes, waitingNode,
                                firstSection.TileX, firstSection.TileZ,
                                firstSection.X, firstSection.Z, (Traveller.TravellerDirection)1);
                float offset = TDBTrav.DistanceTo(waitingNode,
                    stopPathNode.Location.TileX, stopPathNode.Location.TileZ,
                    stopPathNode.Location.Location.X,
                    stopPathNode.Location.Location.Y,
                    stopPathNode.Location.Location.Z);

                int TCSectionIndex = -1;

                for (int iXRef = waitingNode.TCCrossReference.Count - 1; iXRef >= 0 && TCSectionIndex < 0; iXRef--)
                {
                    if (offset <
                     (waitingNode.TCCrossReference[iXRef].OffsetLength[1] + waitingNode.TCCrossReference[iXRef].Length))
                    {
                        TCSectionIndex = waitingNode.TCCrossReference[iXRef].Index;
                    }
                }

                if (TCSectionIndex < 0)
                {
                    TCSectionIndex = waitingNode.TCCrossReference[0].Index;
                }

                return TCSectionIndex;
            }

            //================================================================================================//
            //
            // Check for reversal offset margin
            //

            public void SetReversalOffset(float trainLength, bool timetableMode = true)
            {
                TCReversalInfo thisReversal = ReversalInfo[activeSubpath];
                thisReversal.SignalUsed = thisReversal.Valid && thisReversal.SignalAvailable && timetableMode && trainLength < thisReversal.SignalOffset;
            }

            //================================================================================================//
            //
            // build station xref list
            //

            public void SetStationReference(List<TCSubpathRoute> subpaths, int sectionIndex, Signals orgSignals)
            {
                TrackCircuitSection actSection = orgSignals.TrackCircuitList[sectionIndex];
                foreach (int platformRef in actSection.PlatformIndex)
                {
                    PlatformDetails actPlatform = orgSignals.PlatformDetailsList[platformRef];

                    string stationName = actPlatform.Name.ToLower().Trim();

                    if (!StationXRef.ContainsKey(stationName))
                    {
                        int[] platformInfo = new int[3] { subpaths.Count - 1, subpaths[subpaths.Count - 1].Count - 1, actPlatform.PlatformReference[0] };
                        StationXRef.Add(stationName, platformInfo);
                    }
                }
            }

            //================================================================================================//
            //
            //  Clone subpath at a specific position.  The new subpath will be inserted after the current one and all TCElement from position
            //  To the end will be added to the new subpath.
            //  The WaitingPoint list will be aligned with.
            //

            public void CloneSubPath(int WPIdx, int position)
            {
                int subpathIdx = WaitingPoints[WPIdx][0];
                var subpath = TCRouteSubpaths[subpathIdx];
                TCSubpathRoute nextRoute = new TCSubpathRoute();
                TCRouteSubpaths.Insert(subpathIdx + 1, nextRoute);
                TCReversalInfo nextReversalPoint = new TCReversalInfo(); // also add dummy reversal info to match total number
                //CSComment: insert subpath number and subpath index
                nextReversalPoint.ReversalIndex = subpathIdx + 1;
                ReversalInfo.Insert(subpathIdx + 1, nextReversalPoint);
                LoopEnd.Add(-1); // also add dummy loop end
                for (int iElement = subpath.Count - 1; iElement >= position + 1; iElement--)
                {
                    nextRoute.Insert(0, subpath[iElement]);
                    subpath.RemoveAt(iElement);
                }
                nextRoute.Insert(0, subpath[position]);
                for (int cntWP = WPIdx + 1; cntWP < WaitingPoints.Count; cntWP++)
                {
                    WaitingPoints[cntWP][0]++;
                }
                for (int cntRI = subpathIdx + 2; cntRI < ReversalInfo.Count - 1; cntRI++)
                {
                    if (ReversalInfo[cntRI].ReversalIndex >= 0) ReversalInfo[cntRI].ReversalIndex++;
                }
            }
        }

        //================================================================================================//
        /// <summary>
        /// Track Circuit Route Element
        /// </summary>

        public class TCRouteElement
        {
            public int TCSectionIndex;
            public int Direction;
            public int[] OutPin = new int[2];

            // path based passing path definitions
            public int[] StartAlternativePath;  // if used : index 0 = index of alternative path, index 1 = TC end index
            public int[] EndAlternativePath;    // if used : index 0 = index of alternative path, index 1 = TC start index

            // used for location based passing path processing
            public bool FacingPoint;            // element is facing point
            public int UsedAlternativePath;     // set to index of used alternative path

            //================================================================================================//
            /// <summary>
            /// Constructor from tracknode
            /// </summary>

            public TCRouteElement(TrackNode thisNode, int TCIndex, int direction, Signals mySignals)
            {
                TCSectionIndex = thisNode.TCCrossReference[TCIndex].Index;
                Direction = direction;
                OutPin[0] = direction;
                OutPin[1] = 0;           // always 0 for NORMAL sections, updated for JUNCTION sections

                TrackCircuitSection thisSection = mySignals.TrackCircuitList[TCSectionIndex];
                if (thisSection.CircuitType == TrackCircuitSection.TrackCircuitType.Crossover)
                {
                    int outPinLink = direction;
                    int nextIndex = thisNode.TCCrossReference[TCIndex + 1].Index;
                    if (direction == 1)
                    {
                        nextIndex = thisNode.TCCrossReference[TCIndex - 1].Index;
                    }
                    OutPin[1] = (thisSection.Pins[outPinLink, 0].Link == nextIndex) ? 0 : 1;
                }

                FacingPoint = false;
                if (thisSection.CircuitType == TrackCircuitSection.TrackCircuitType.Junction)
                {
                    if (thisSection.Pins[direction, 1].Link != -1)
                    {
                        FacingPoint = true;
                    }
                }

                UsedAlternativePath = -1;
            }

            //================================================================================================//
            /// <summary>
            /// Constructor from CircuitSection
            /// </summary>

            public TCRouteElement(TrackCircuitSection thisSection, int direction, Signals mySignals, int lastSectionIndex)
            {
                TCSectionIndex = thisSection.Index;
                Direction = direction;
                OutPin[0] = direction;
                OutPin[1] = 0;           // always 0 for NORMAL sections, updated for JUNCTION sections

                if (thisSection.CircuitType == TrackCircuitSection.TrackCircuitType.Crossover)
                {
                    int inPinLink = direction == 0 ? 1 : 0;
                    OutPin[1] = (thisSection.Pins[inPinLink, 0].Link == lastSectionIndex) ? 0 : 1;
                }

                FacingPoint = false;
                if (thisSection.CircuitType == TrackCircuitSection.TrackCircuitType.Junction)
                {
                    if (thisSection.Pins[direction, 1].Link != -1)
                    {
                        FacingPoint = true;
                    }
                }

                UsedAlternativePath = -1;
            }

            //================================================================================================//
            /// <summary>
            /// Constructor for additional items for route checking (not part of train route, NORMAL items only)
            /// </summary>

            public TCRouteElement(int TCIndex, int direction)
            {
                TCSectionIndex = TCIndex;
                Direction = direction;
                OutPin[0] = direction;
                OutPin[1] = 0;
                UsedAlternativePath = -1;
            }

            //================================================================================================//
            //
            // Constructor from other route element
            //

            public TCRouteElement(TCRouteElement otherElement)
            {
                TCSectionIndex = otherElement.TCSectionIndex;
                Direction = otherElement.Direction;

                OutPin = new int[2];
                otherElement.OutPin.CopyTo(OutPin, 0);

                if (otherElement.StartAlternativePath != null)
                {
                    StartAlternativePath = new int[2];
                    otherElement.StartAlternativePath.CopyTo(StartAlternativePath, 0);
                }

                if (otherElement.EndAlternativePath != null)
                {
                    EndAlternativePath = new int[2];
                    otherElement.EndAlternativePath.CopyTo(EndAlternativePath, 0);
                }

                FacingPoint = otherElement.FacingPoint;
                UsedAlternativePath = otherElement.UsedAlternativePath;
            }

            //================================================================================================//
            //
            // Restore
            //

            public TCRouteElement(BinaryReader inf)
            {
                TCSectionIndex = inf.ReadInt32();
                Direction = inf.ReadInt32();
                OutPin[0] = inf.ReadInt32();
                OutPin[1] = inf.ReadInt32();

                int altindex = inf.ReadInt32();
                if (altindex >= 0)
                {
                    StartAlternativePath = new int[2];
                    StartAlternativePath[0] = altindex;
                    StartAlternativePath[1] = inf.ReadInt32();
                }

                altindex = inf.ReadInt32();
                if (altindex >= 0)
                {
                    EndAlternativePath = new int[2];
                    EndAlternativePath[0] = altindex;
                    EndAlternativePath[1] = inf.ReadInt32();
                }

                FacingPoint = inf.ReadBoolean();
                UsedAlternativePath = inf.ReadInt32();
            }

            //================================================================================================//
            //
            // Save
            //

            public void Save(BinaryWriter outf)
            {
                outf.Write(TCSectionIndex);
                outf.Write(Direction);
                outf.Write(OutPin[0]);
                outf.Write(OutPin[1]);

                if (StartAlternativePath != null)
                {
                    outf.Write(StartAlternativePath[0]);
                    outf.Write(StartAlternativePath[1]);
                }
                else
                {
                    outf.Write(-1);
                }


                if (EndAlternativePath != null)
                {
                    outf.Write(EndAlternativePath[0]);
                    outf.Write(EndAlternativePath[1]);
                }
                else
                {
                    outf.Write(-1);
                }

                outf.Write(FacingPoint);
                outf.Write(UsedAlternativePath);
            }
        }

        //================================================================================================//
        /// <summary>
        /// Subpath list : list of TCRouteElements building a subpath
        /// </summary>

        public class TCSubpathRoute : List<TCRouteElement>
        {


            //================================================================================================//
            //
            // Base contstructor
            //

            public TCSubpathRoute()
            {
            }


            //================================================================================================//
            //
            // Constructor from existing subpath
            //

            public TCSubpathRoute(TCSubpathRoute otherSubpathRoute)
            {
                if (otherSubpathRoute != null)
                {
                    for (int iIndex = 0; iIndex < otherSubpathRoute.Count; iIndex++)
                    {
                        this.Add(otherSubpathRoute[iIndex]);
                    }
                }
            }

            //================================================================================================//
            //
            // Constructor from part of existing subpath
            // if either value is < 0, start from start or stop at end
            //

            public TCSubpathRoute(TCSubpathRoute otherSubpathRoute, int startIndex, int endIndex)
            {
                int lstartIndex = startIndex >= 0 ? startIndex : 0;
                int lendIndex = otherSubpathRoute.Count - 1;
                lendIndex = endIndex >= 0 ? Math.Min(lendIndex, endIndex) : lendIndex;

                if (otherSubpathRoute != null)
                {
                    for (int iIndex = lstartIndex; iIndex <= lendIndex; iIndex++)
                    {
                        this.Add(new TCRouteElement(otherSubpathRoute[iIndex]));
                    }
                }
            }

            //================================================================================================//
            //
            // Restore
            //

            public TCSubpathRoute(BinaryReader inf)
            {
                int totalElements = inf.ReadInt32();

                for (int iElements = 0; iElements < totalElements; iElements++)
                {
                    TCRouteElement thisElement = new TCRouteElement(inf);
                    this.Add(thisElement);
                }
            }

            //================================================================================================//
            //
            // Save
            //

            public void Save(BinaryWriter outf)
            {
                outf.Write(this.Count);
                foreach (TCRouteElement thisElement in this)
                {
                    thisElement.Save(outf);
                }
            }

            //================================================================================================//
            /// <summary>
            /// Get sectionindex in subpath
            /// <\summary>

            public int GetRouteIndex(int thisSectionIndex, int startIndex)
            {
                for (int iNode = startIndex; iNode >= 0 && iNode < this.Count; iNode++)
                {
                    Train.TCRouteElement thisElement = this[iNode];
                    if (thisElement.TCSectionIndex == thisSectionIndex)
                    {
                        return (iNode);
                    }
                }

                return (-1);
            }

            //================================================================================================//
            /// <summary>
            /// Get sectionindex in subpath
            /// <\summary>

            public int GetRouteIndexBackward(int thisSectionIndex, int startIndex)
            {
                for (int iNode = startIndex - 1; iNode >= 0 && iNode < this.Count; iNode--)
                {
                    Train.TCRouteElement thisElement = this[iNode];
                    if (thisElement.TCSectionIndex == thisSectionIndex)
                    {
                        return (iNode);
                    }
                }

                return (-1);
            }

            //================================================================================================//
            /// <summary>
            /// returns if signal is ahead of train
            /// <\summary>

            public bool SignalIsAheadOfTrain(SignalObject thisSignal, TCPosition trainPosition)
            {
                int signalSection = thisSignal.TCReference;
                int signalRouteIndex = GetRouteIndexBackward(signalSection, trainPosition.RouteListIndex);
                if (signalRouteIndex >= 0)
                    return (false);  // signal section passed earlier in route
                signalRouteIndex = GetRouteIndex(signalSection, trainPosition.RouteListIndex);
                if (signalRouteIndex >= 0)
                    return (true); // signal section still ahead

                if (trainPosition.TCSectionIndex == thisSignal.TCNextTC)
                    return (false); // if train in section following signal, assume we passed

                // signal is not on route - assume we did not pass

#if DEBUG_REPORTS
                int trainno = (thisSignal.enabledTrain != null) ? thisSignal.enabledTrain.Train.Number : -1;

                File.AppendAllText(@"C:\temp\printproc.txt", "Cannot find signal on route : " +
                                " Train " + trainno.ToString() +
                                ", Signal : " + thisSignal.thisRef.ToString() +
                                " in section " + thisSignal.TCReference.ToString() +
                                ", starting from section " + trainPosition.TCSectionIndex.ToString() + "\n");
#endif
                if (thisSignal.enabledTrain != null && thisSignal.enabledTrain.Train.CheckTrain)
                {
                    int trainnoCT = (thisSignal.enabledTrain != null) ? thisSignal.enabledTrain.Train.Number : -1;

                    File.AppendAllText(@"C:\temp\checktrain.txt", "Cannot find signal on route : " +
                                    " Train " + trainnoCT.ToString() +
                                    ", Signal : " + thisSignal.thisRef.ToString() +
                                    " in section " + thisSignal.TCReference.ToString() +
                                    ", starting from section " + trainPosition.TCSectionIndex.ToString() + "\n");
                }
                return (true);
            }

            //================================================================================================//
            /// <summary>
            /// returns distance along route
            /// <\summary>

            public float GetDistanceAlongRoute(int startSectionIndex, float startOffset,
               int endSectionIndex, float endOffset, bool forward, Signals signals)

            // startSectionIndex and endSectionIndex are indices in route list
            // startOffset is remaining length of startSection in required direction
            // endOffset is length along endSection in required direction
            {
                float totalLength = startOffset;

                if (startSectionIndex == endSectionIndex)
                {
                    TrackCircuitSection thisSection = signals.TrackCircuitList[this[startSectionIndex].TCSectionIndex];
                    totalLength = startOffset - (thisSection.Length - endOffset);
                    return (totalLength);
                }

                if (forward)
                {
                    if (startSectionIndex > endSectionIndex)
                        return (-1);

                    for (int iIndex = startSectionIndex + 1; iIndex < endSectionIndex; iIndex++)
                    {
                        TrackCircuitSection thisSection = signals.TrackCircuitList[this[iIndex].TCSectionIndex];
                        totalLength += thisSection.Length;
                    }
                }
                else
                {
                    if (startSectionIndex < endSectionIndex)
                        return (-1);

                    for (int iIndex = startSectionIndex - 1; iIndex > endSectionIndex; iIndex--)
                    {
                        TrackCircuitSection thisSection = signals.TrackCircuitList[this[iIndex].TCSectionIndex];
                        totalLength += thisSection.Length;
                    }
                }

                totalLength += endOffset;

                return (totalLength);
            }

            //================================================================================================//
            /// <summary>
            /// returns if position is ahead of train
            /// <\summary>

            // without offset
            public static bool IsAheadOfTrain(TrackCircuitSection thisSection, TCPosition trainPosition)
            {
                float distanceAhead = thisSection.GetDistanceBetweenObjects(
                    trainPosition.TCSectionIndex, trainPosition.TCOffset, trainPosition.TCDirection,
                        thisSection.Index, 0.0f);
                return (distanceAhead > 0.0f);
            }

            // with offset
            public static bool IsAheadOfTrain(TrackCircuitSection thisSection, float offset, TCPosition trainPosition)
            {
                float distanceAhead = thisSection.GetDistanceBetweenObjects(
                    trainPosition.TCSectionIndex, trainPosition.TCOffset, trainPosition.TCDirection,
                        thisSection.Index, offset);
                return (distanceAhead > 0.0f);
            }

            //================================================================================================//
            //
            // Converts list of elements to dictionary
            //

            public Dictionary<int, int> ConvertRoute()
            {
                Dictionary<int, int> thisDict = new Dictionary<int, int>();

                foreach (TCRouteElement thisElement in this)
                {
                    if (!thisDict.ContainsKey(thisElement.TCSectionIndex))
                    {
                        thisDict.Add(thisElement.TCSectionIndex, thisElement.Direction);
                    }
                }

                return (thisDict);
            }

            //================================================================================================//
            /// <summary>
            /// check if subroute contains section
            /// <\summary>

            public bool ContainsSection(TCRouteElement thisElement)
            {
                // convert route to dictionary

                Dictionary<int, int> thisRoute = ConvertRoute();
                return (thisRoute.ContainsKey(thisElement.TCSectionIndex));
            }


            //================================================================================================//
            /// <summary>
            /// Find actual diverging path from alternative path definition
            /// Returns : [0,*] = Main Route, [1,*] = Alt Route, [*,0] = Start Index, [*,1] = End Index
            /// <\summary>

            public int[,] FindActualDivergePath(TCSubpathRoute altRoute, int startIndex, int endIndex)
            {
                int[,] returnValue = new int[2, 2] { { -1, -1 }, { -1, -1 } };

                bool firstfound = false;
                bool lastfound = false;

                int MainPathActualStartRouteIndex = -1;
                int MainPathActualEndRouteIndex = -1;
                int AltPathActualStartRouteIndex = -1;
                int AltPathActualEndRouteIndex = -1;

                for (int iIndex = 0; iIndex < altRoute.Count && !firstfound; iIndex++)
                {
                    int mainIndex = iIndex + startIndex;
                    if (altRoute[iIndex].TCSectionIndex != this[mainIndex].TCSectionIndex)
                    {
                        firstfound = true;
                        MainPathActualStartRouteIndex = mainIndex;
                        AltPathActualStartRouteIndex = iIndex;
                    }
                }

                for (int iIndex = 0; iIndex < altRoute.Count && firstfound && !lastfound; iIndex++)
                {
                    int altIndex = altRoute.Count - 1 - iIndex;
                    int mainIndex = endIndex - iIndex;
                    if (altRoute[altIndex].TCSectionIndex != this[mainIndex].TCSectionIndex)
                    {
                        lastfound = true;
                        MainPathActualEndRouteIndex = mainIndex;
                        AltPathActualEndRouteIndex = altIndex;
                    }
                }

                if (lastfound)
                {
                    returnValue[0, 0] = MainPathActualStartRouteIndex;
                    returnValue[0, 1] = MainPathActualEndRouteIndex;
                    returnValue[1, 0] = AltPathActualStartRouteIndex;
                    returnValue[1, 1] = AltPathActualEndRouteIndex;
                }

                return (returnValue);
            }

            //================================================================================================//
            /// <summary>
            /// Get usefull length
            /// Returns : dictionary with : 
            ///    key is last section to be used in path (before signal or node)
            ///    value is usefull length
            /// <\summary>

            public Dictionary<int, float> GetUsefullLength(float defaultSignalClearingDistance, Signals signals, int startIndex, int endIndex)
            {
                float actLength = 0.0f;
                float useLength = 0.0f;
                bool endSignal = false;

                int usedStartIndex = (startIndex >= 0) ? startIndex : 0;
                int usedEndIndex = (endIndex > 0 && endIndex <= Count - 1) ? endIndex : Count - 1;
                int lastUsedIndex = startIndex;

                // first junction
                TrackCircuitSection firstSection = signals.TrackCircuitList[this[usedStartIndex].TCSectionIndex];
                if (firstSection.CircuitType == TrackCircuitSection.TrackCircuitType.Junction)
                {
                    actLength = firstSection.Length - (float)(2 * firstSection.Overlap);
                }
                else
                {
                    actLength = firstSection.Length;
                }

                useLength = actLength;

                // intermediate sections

                for (int iSection = usedStartIndex + 1; iSection < usedEndIndex - 1; iSection++)
                {
                    TCRouteElement thisElement = this[iSection];
                    TrackCircuitSection thisSection = signals.TrackCircuitList[thisElement.TCSectionIndex];
                    actLength += thisSection.Length;

                    // if section has end signal, set usefull length upto this point
                    if (thisSection.EndSignals[thisElement.Direction] != null)
                    {
                        useLength = actLength - (2 * defaultSignalClearingDistance);
                        endSignal = true;
                        lastUsedIndex = iSection - usedStartIndex;
                    }
                }

                // last section if no signal found

                if (!endSignal)
                {
                    TrackCircuitSection lastSection = signals.TrackCircuitList[this[usedEndIndex].TCSectionIndex];
                    if (lastSection.CircuitType == TrackCircuitSection.TrackCircuitType.Junction)
                    {
                        actLength += (lastSection.Length - (float)(2 * lastSection.Overlap));
                        lastUsedIndex = usedEndIndex - usedStartIndex - 1;
                    }
                    else
                    {
                        actLength += lastSection.Length;
                        lastUsedIndex = usedEndIndex - usedStartIndex;
                    }

                    useLength = actLength;
                }

                return (new Dictionary<int, float>() { { lastUsedIndex, useLength } });
            }

            //================================================================================================//
            /// <summary>
            /// compares if equal to other path
            /// paths must be exactly equal (no part check)
            /// <\summary>

            public bool EqualsPath(TCSubpathRoute otherRoute)
            {
                // check common route parts

                if (Count != otherRoute.Count) return (false);  // if path lengths are unequal they cannot be the same

                bool equalPath = true;

                for (int iIndex = 0; iIndex < Count - 1; iIndex++)
                {
                    if (this[iIndex].TCSectionIndex != otherRoute[iIndex].TCSectionIndex)
                    {
                        equalPath = false;
                        break;
                    }
                }

                return (equalPath);
            }

            //================================================================================================//
            /// <summary>
            /// compares if equal to other path in reverse
            /// paths must be exactly equal (no part check)
            /// <\summary>

            public bool EqualsReversePath(TCSubpathRoute otherRoute)
            {
                // check common route parts

                if (Count != otherRoute.Count) return (false);  // if path lengths are unequal they cannot be the same

                bool equalPath = true;

                for (int iIndex = 0; iIndex < Count - 1; iIndex++)
                {
                    if (this[iIndex].TCSectionIndex != otherRoute[otherRoute.Count - 1 - iIndex].TCSectionIndex)
                    {
                        equalPath = false;
                        break;
                    }
                }

                return (equalPath);
            }

            //================================================================================================//
            /// <summary>
            /// reverses existing path
            /// <\summary>

            public TCSubpathRoute ReversePath(Signals orgSignals)
            {
                TCSubpathRoute reversePath = new TCSubpathRoute();
                int lastSectionIndex = -1;

                for (int iIndex = Count - 1; iIndex >= 0; iIndex--)
                {
                    TCRouteElement thisElement = this[iIndex];
                    int newDirection = (thisElement.Direction == 0) ? 1 : 0;
                    TrackCircuitSection thisSection = orgSignals.TrackCircuitList[thisElement.TCSectionIndex];

                    TCRouteElement newElement = new TCRouteElement(thisSection, newDirection, orgSignals, lastSectionIndex);

                    // reset outpin for JUNCTION
                    // if trailing, pin[0] = 0, pin[1] = 0
                    // if facing, pin[0] = 1, check next element for pin[1]

                    if (thisSection.CircuitType == TrackCircuitSection.TrackCircuitType.Junction)
                    {
                        if (newElement.FacingPoint)
                        {
                            if (iIndex >= 1)
                            {
                                newElement.OutPin[0] = 1;
                                newElement.OutPin[1] = (thisSection.Pins[1, 0].Link == this[iIndex - 1].TCSectionIndex) ? 0 : 1;
                            }
                        }
                        else
                        {
                            newElement.OutPin[0] = 0;
                            newElement.OutPin[1] = 0;
                        }
                    }

                    reversePath.Add(newElement);
                    lastSectionIndex = thisElement.TCSectionIndex;
                }

                return (reversePath);
            }
        }// end class TCSubpathRoute

        //================================================================================================//
        /// <summary>
        /// TrackCircuit position class
        /// </summary>

        public class TCPosition
        {
            public int TCSectionIndex;
            public int TCDirection;
            public float TCOffset;
            public int RouteListIndex;
            public int TrackNode;
            public float DistanceTravelledM;

            //================================================================================================//
            /// <summary>
            /// constructor - creates empty item
            /// </summary>

            public TCPosition()
            {
                TCSectionIndex = -1;
                TCDirection = 0;
                TCOffset = 0.0f;
                RouteListIndex = -1;
                TrackNode = -1;
                DistanceTravelledM = 0.0f;
            }

            //================================================================================================//
            //
            // Restore
            //

            public void RestorePresentPosition(BinaryReader inf, Train train)
            {
                TrackNode tn = train.FrontTDBTraveller.TN;
                float offset = train.FrontTDBTraveller.TrackNodeOffset;
                int direction = (int)train.FrontTDBTraveller.Direction;

                TCPosition tempPosition = new TCPosition();
                tempPosition.SetTCPosition(tn.TCCrossReference, offset, direction);

                TCSectionIndex = inf.ReadInt32();
                TCDirection = inf.ReadInt32();
                TCOffset = inf.ReadSingle();
                RouteListIndex = inf.ReadInt32();
                TrackNode = inf.ReadInt32();
                DistanceTravelledM = inf.ReadSingle();

                float offsetDif = Math.Abs(TCOffset - tempPosition.TCOffset);
                if (TCSectionIndex != tempPosition.TCSectionIndex ||
                        (TCSectionIndex == tempPosition.TCSectionIndex && offsetDif > 5.0f))
                {
                    Trace.TraceWarning("Train {0} restored at different present position : was {1} - {3}, is {2} - {4}",
                            train.Number, TCSectionIndex, tempPosition.TCSectionIndex,
                            TCOffset, tempPosition.TCOffset);
                }
            }


            public void RestorePresentRear(BinaryReader inf, Train train)
            {
                TrackNode tn = train.RearTDBTraveller.TN;
                float offset = train.RearTDBTraveller.TrackNodeOffset;
                int direction = (int)train.RearTDBTraveller.Direction;

                TCPosition tempPosition = new TCPosition();
                tempPosition.SetTCPosition(tn.TCCrossReference, offset, direction);

                TCSectionIndex = inf.ReadInt32();
                TCDirection = inf.ReadInt32();
                TCOffset = inf.ReadSingle();
                RouteListIndex = inf.ReadInt32();
                TrackNode = inf.ReadInt32();
                DistanceTravelledM = inf.ReadSingle();

                float offsetDif = Math.Abs(TCOffset - tempPosition.TCOffset);
                if (TCSectionIndex != tempPosition.TCSectionIndex ||
                        (TCSectionIndex == tempPosition.TCSectionIndex && offsetDif > 5.0f))
                {
                    Trace.TraceWarning("Train {0} restored at different present rear : was {1}-{2}, is {3}-{4}",
                            train.Number, TCSectionIndex, tempPosition.TCSectionIndex,
                            TCOffset, tempPosition.TCOffset);
                }
            }


            public void RestorePreviousPosition(BinaryReader inf)
            {
                TCSectionIndex = inf.ReadInt32();
                TCDirection = inf.ReadInt32();
                TCOffset = inf.ReadSingle();
                RouteListIndex = inf.ReadInt32();
                TrackNode = inf.ReadInt32();
                DistanceTravelledM = inf.ReadSingle();
            }


            //================================================================================================//
            //
            // Restore dummies for trains not yet started
            //

            public void RestorePresentPositionDummy(BinaryReader inf, Train train)
            {
                TCSectionIndex = inf.ReadInt32();
                TCDirection = inf.ReadInt32();
                TCOffset = inf.ReadSingle();
                RouteListIndex = inf.ReadInt32();
                TrackNode = inf.ReadInt32();
                DistanceTravelledM = inf.ReadSingle();
            }


            public void RestorePresentRearDummy(BinaryReader inf, Train train)
            {
                TCSectionIndex = inf.ReadInt32();
                TCDirection = inf.ReadInt32();
                TCOffset = inf.ReadSingle();
                RouteListIndex = inf.ReadInt32();
                TrackNode = inf.ReadInt32();
                DistanceTravelledM = inf.ReadSingle();
            }


            public void RestorePreviousPositionDummy(BinaryReader inf)
            {
                TCSectionIndex = inf.ReadInt32();
                TCDirection = inf.ReadInt32();
                TCOffset = inf.ReadSingle();
                RouteListIndex = inf.ReadInt32();
                TrackNode = inf.ReadInt32();
                DistanceTravelledM = inf.ReadSingle();
            }

            //================================================================================================//
            //
            // Save
            //

            public void Save(BinaryWriter outf)
            {
                outf.Write(TCSectionIndex);
                outf.Write(TCDirection);
                outf.Write(TCOffset);
                outf.Write(RouteListIndex);
                outf.Write(TrackNode);
                outf.Write(DistanceTravelledM);
            }

            //================================================================================================//
            /// <summary>
            /// Copy TCPosition
            /// <\summary>

            public void CopyTo(ref TCPosition thisPosition)
            {
                thisPosition.TCSectionIndex = this.TCSectionIndex;
                thisPosition.TCDirection = this.TCDirection;
                thisPosition.TCOffset = this.TCOffset;
                thisPosition.RouteListIndex = this.RouteListIndex;
                thisPosition.TrackNode = this.TrackNode;
                thisPosition.DistanceTravelledM = this.DistanceTravelledM;
            }

            //================================================================================================//
            /// <summary>
            /// Reverse (or continue in same direction)
            /// <\summary>

            public void Reverse(int oldDirection, TCSubpathRoute thisRoute, float offset, Signals orgSignals)
            {
                RouteListIndex = thisRoute.GetRouteIndex(TCSectionIndex, 0);
                if (RouteListIndex >= 0)
                {
                    TCDirection = thisRoute[RouteListIndex].Direction;
                }
                else
                {
                    TCDirection = TCDirection == 0 ? 1 : 0;
                }

                TrackCircuitSection thisSection = orgSignals.TrackCircuitList[TCSectionIndex];
                if (oldDirection != TCDirection)
                    TCOffset = thisSection.Length - TCOffset; // actual reversal so adjust offset

                DistanceTravelledM = offset;
            }

            /// <summary>
            /// Set the position based on the trackcircuit section.
            /// </summary>
            /// <param name="trackCircuitXRefList">List of cross-references from tracknode to trackcircuitsection</param>
            /// <param name="offset">Offset along the tracknode</param>
            /// <param name="direction">direction along the tracknode (1 is forward)</param>
            public void SetTCPosition(TrackCircuitXRefList trackCircuitXRefList, float offset, int direction)
            {
                int XRefIndex = trackCircuitXRefList.GetXRefIndex(offset, direction);

                if (XRefIndex < 0) return;

                TrackCircuitSectionXref thisReference = trackCircuitXRefList[XRefIndex];
                this.TCSectionIndex = thisReference.Index;
                this.TCDirection = direction;
                this.TCOffset = offset - thisReference.OffsetLength[direction];
            }
        }

        //================================================================================================//
        /// <summary>
        /// Reversal information class
        /// </summary>

        public class TCReversalInfo
        {
            public bool Valid;
            public int LastDivergeIndex;
            public int FirstDivergeIndex;
            public int DivergeSectorIndex;
            public float DivergeOffset;
            public bool SignalAvailable;
            public bool SignalUsed;
            public int LastSignalIndex;
            public int FirstSignalIndex;
            public int SignalSectorIndex;
            public float SignalOffset;
            public float ReverseReversalOffset;
            public int ReversalIndex;
            public int ReversalSectionIndex;
            public bool ReversalActionInserted;

            //================================================================================================//
            /// <summary>
            /// Constructor (from route path details)
            /// <\summary>

            public TCReversalInfo(TCSubpathRoute lastRoute, int prevReversalIndex, TCSubpathRoute firstRoute, Signals orgSignals, float reverseReversalOffset, int reversalIndex, int reversalSectionIndex)
            {
                // preset values
                Valid = false;
                LastDivergeIndex = -1;
                FirstDivergeIndex = -1;
                LastSignalIndex = -1;
                FirstSignalIndex = -1;
                SignalAvailable = false;
                SignalUsed = false;
                ReverseReversalOffset = reverseReversalOffset;
                ReversalIndex = reversalIndex;
                ReversalSectionIndex = reversalSectionIndex;
                ReversalActionInserted = false;

                // search for first common section in last and first

                int lastIndex = lastRoute.Count - 1;
                int firstIndex = 0;

                int lastCommonSection = -1;
                int firstCommonSection = -1;

                bool commonFound = false;
                bool validDivPoint = false;

                while (!commonFound && lastIndex >= 0)
                {
                    TCRouteElement lastElement = lastRoute[lastIndex];

                    while (!commonFound && firstIndex <= firstRoute.Count - 1)
                    {
                        TCRouteElement firstElement = firstRoute[firstIndex];
                        if (lastElement.TCSectionIndex == firstElement.TCSectionIndex)
                        {
                            commonFound = true;
                            lastCommonSection = lastIndex;
                            firstCommonSection = firstIndex;

                            Valid = (lastElement.Direction != firstElement.Direction);
                        }
                        else
                        {
                            firstIndex++;
                        }
                    }
                    lastIndex--;
                    firstIndex = 0;
                }

                // search for last common section going backward along route
                // do not go back on last route beyond previous reversal point to prevent fall through of reversals
                if (Valid)
                {
                    Valid = false;

                    lastIndex = lastCommonSection;
                    firstIndex = firstCommonSection;

                    int endLastIndex = (prevReversalIndex > 0 && prevReversalIndex < lastCommonSection &&
                        orgSignals.Simulator.TimetableMode) ? prevReversalIndex : 0;

                    while (lastIndex >= endLastIndex && firstIndex <= (firstRoute.Count - 1) && lastRoute[lastIndex].TCSectionIndex == firstRoute[firstIndex].TCSectionIndex)
                    {
                        LastDivergeIndex = lastIndex;
                        FirstDivergeIndex = firstIndex;
                        DivergeSectorIndex = lastRoute[lastIndex].TCSectionIndex;

                        lastIndex--;
                        firstIndex++;
                    }

                    // if next route ends within last one, last diverge index can be set to endLastIndex
                    if (firstIndex > firstRoute.Count -1)
                    {
                        LastDivergeIndex = endLastIndex;
                        DivergeSectorIndex = lastRoute[endLastIndex].TCSectionIndex;
                    }

                    Valid = LastDivergeIndex >= 0; // it is a reversal
                    validDivPoint = true;
                    if (orgSignals.Simulator.TimetableMode)
                        validDivPoint = LastDivergeIndex > 0 && FirstDivergeIndex < (firstRoute.Count - 1); // valid reversal point
                    if (lastRoute.Count == 1 && FirstDivergeIndex < (firstRoute.Count - 1)) validDivPoint = true; // valid reversal point in first and only section
                }

                // determine offset

                if (validDivPoint)
                {
                    DivergeOffset = 0.0f;
                    for (int iSection = LastDivergeIndex; iSection < lastRoute.Count; iSection++)
                    {
                        TrackCircuitSection thisSection = orgSignals.TrackCircuitList[lastRoute[iSection].TCSectionIndex];
                        DivergeOffset += thisSection.Length;
                    }

                    // find last signal furthest away from diverging point

                    bool signalFound = false;
                    int startSection = 0;

                    if (!orgSignals.Simulator.TimetableMode)
                    // In activity mode test starts only after reverse point.
                    {
                        for (int iSection = 0; iSection < firstRoute.Count; iSection++)
                        {
                            if (firstRoute[iSection].TCSectionIndex == ReversalSectionIndex)
                            {
                                startSection = iSection;
                                break;
                            }
                        }
                        for (int iSection = startSection; iSection <= FirstDivergeIndex && !signalFound; iSection++)
                        {
                            TrackCircuitSection thisSection = orgSignals.TrackCircuitList[firstRoute[iSection].TCSectionIndex];
                            if (thisSection.EndSignals[firstRoute[iSection].Direction] != null)   // signal in required direction
                            {
                                signalFound = true;
                                FirstSignalIndex = iSection;
                                SignalSectorIndex = thisSection.Index;
                            }
                        }
                    }
                    // in timetable mode, search for first signal beyond diverging point
                    else
                    {
                        for (int iSection = FirstDivergeIndex; iSection >= startSection && !signalFound; iSection--)
                        {
                            TrackCircuitSection thisSection = orgSignals.TrackCircuitList[firstRoute[iSection].TCSectionIndex];
                            if (thisSection.EndSignals[firstRoute[iSection].Direction] != null)   // signal in required direction
                            {
                                signalFound = true;
                                FirstSignalIndex = iSection;
                                SignalSectorIndex = thisSection.Index;
                            }
                        }
                    }

                    // signal found
                    if (signalFound)
                    {
                        LastSignalIndex = lastRoute.GetRouteIndex(SignalSectorIndex, LastDivergeIndex);
                        if (LastSignalIndex > 0)
                        {
                            SignalAvailable = true;

                            SignalOffset = 0.0f;
                            for (int iSection = LastSignalIndex; iSection < lastRoute.Count; iSection++)
                            {
                                TrackCircuitSection thisSection = orgSignals.TrackCircuitList[lastRoute[iSection].TCSectionIndex];
                                SignalOffset += thisSection.Length;
                            }
                        }
                    }
                }
                else
                {
                    FirstDivergeIndex = -1;
                    LastDivergeIndex = -1;
                }

            }//constructor

            //================================================================================================//
            /// <summary>
            /// Constructor (from copy)
            /// <\summary>

            public TCReversalInfo(TCReversalInfo otherInfo)
            {
                Valid = otherInfo.Valid;

                LastDivergeIndex = otherInfo.LastDivergeIndex;
                FirstDivergeIndex = otherInfo.FirstDivergeIndex;
                DivergeSectorIndex = otherInfo.DivergeSectorIndex;
                DivergeOffset = otherInfo.DivergeOffset;

                SignalAvailable = otherInfo.SignalAvailable;
                SignalUsed = otherInfo.SignalUsed;
                LastSignalIndex = otherInfo.LastSignalIndex;
                FirstSignalIndex = otherInfo.FirstSignalIndex;
                SignalSectorIndex = otherInfo.SignalSectorIndex;
                SignalOffset = otherInfo.SignalOffset;
                ReverseReversalOffset = otherInfo.ReverseReversalOffset;
                ReversalIndex = otherInfo.ReversalIndex;
                ReversalSectionIndex = otherInfo.ReversalSectionIndex;
                ReversalActionInserted = false;
            }

            //================================================================================================//
            /// <summary>
            /// Constructor (for invalid item)
            /// <\summary>

            public TCReversalInfo()
            {
                // preset values
                Valid = false;

                LastDivergeIndex = -1;
                FirstDivergeIndex = -1;
                DivergeSectorIndex = -1;
                DivergeOffset = 0.0f;

                LastSignalIndex = -1;
                FirstSignalIndex = -1;
                SignalSectorIndex = -1;
                SignalOffset = 0.0f;

                SignalAvailable = false;
                SignalUsed = false;
                ReverseReversalOffset = 0.0f;
                ReversalIndex = -1;
                ReversalSectionIndex = -1;
                ReversalActionInserted = false;
            }

            //================================================================================================//
            /// <summary>
            /// Constructor for Restore
            /// <\summary>

            public TCReversalInfo(BinaryReader inf)
            {
                Valid = inf.ReadBoolean();
                LastDivergeIndex = inf.ReadInt32();
                FirstDivergeIndex = inf.ReadInt32();
                DivergeSectorIndex = inf.ReadInt32();
                DivergeOffset = inf.ReadSingle();

                SignalAvailable = inf.ReadBoolean();
                SignalUsed = inf.ReadBoolean();
                LastSignalIndex = inf.ReadInt32();
                FirstSignalIndex = inf.ReadInt32();
                SignalSectorIndex = inf.ReadInt32();
                SignalOffset = inf.ReadSingle();
                ReverseReversalOffset = inf.ReadSingle();
                ReversalIndex = inf.ReadInt32();
                ReversalSectionIndex = inf.ReadInt32();
                ReversalActionInserted = inf.ReadBoolean();
            }

            //================================================================================================//
            /// <summary>
            /// Save
            /// <\summary>

            public void Save(BinaryWriter outf)
            {
                outf.Write(Valid);
                outf.Write(LastDivergeIndex);
                outf.Write(FirstDivergeIndex);
                outf.Write(DivergeSectorIndex);
                outf.Write(DivergeOffset);
                outf.Write(SignalAvailable);
                outf.Write(SignalUsed);
                outf.Write(LastSignalIndex);
                outf.Write(FirstSignalIndex);
                outf.Write(SignalSectorIndex);
                outf.Write(SignalOffset);
                outf.Write(ReverseReversalOffset);
                outf.Write(ReversalIndex);
                outf.Write(ReversalSectionIndex);
                outf.Write(ReversalActionInserted);
            }

        }//TCReversalInfo

        //================================================================================================//
        /// <summary>
        /// Rough Reversal information class, used only during route building.
        /// </summary>

        public class RoughReversalInfo
        {
            public int SubPathIndex;
            public float ReverseReversalOffset;
            public int ReversalSectionIndex;

            //================================================================================================//
            /// <summary>
            /// Constructor (from route path details)
            /// <\summary>

            public RoughReversalInfo(int subPathIndex, float reverseReversalOffset, int reversalSectionIndex)
            {


                SubPathIndex = subPathIndex;
                ReverseReversalOffset = reverseReversalOffset;
                ReversalSectionIndex = reversalSectionIndex;
            }
        }


        //================================================================================================//
        /// <summary>
        /// Distance Travelled action item list
        /// </summary>

        public class DistanceTravelledActions : LinkedList<DistanceTravelledItem>
        {

            //================================================================================================//
            //
            // Copy list
            //

            public DistanceTravelledActions Copy()
            {
                DistanceTravelledActions newList = new DistanceTravelledActions();

                LinkedListNode<DistanceTravelledItem> nextNode = this.First;
                DistanceTravelledItem thisItem = nextNode.Value;

                newList.AddFirst(thisItem);
                LinkedListNode<DistanceTravelledItem> prevNode = newList.First;

                nextNode = nextNode.Next;

                while (nextNode != null)
                {
                    thisItem = nextNode.Value;
                    newList.AddAfter(prevNode, thisItem);
                    nextNode = nextNode.Next;
                    prevNode = prevNode.Next;
                }

                return (newList);
            }


            //================================================================================================//
            /// <summary>
            /// Insert item on correct distance
            /// <\summary>

            public void InsertAction(DistanceTravelledItem thisItem)
            {

                if (this.Count == 0)
                {
                    this.AddFirst(thisItem);
                }
                else
                {
                    LinkedListNode<DistanceTravelledItem> nextNode = this.First;
                    DistanceTravelledItem nextItem = nextNode.Value;
                    bool inserted = false;
                    while (!inserted)
                    {
                        if (thisItem.RequiredDistance < nextItem.RequiredDistance)
                        {
                            this.AddBefore(nextNode, thisItem);
                            inserted = true;
                        }
                        else if (nextNode.Next == null)
                        {
                            this.AddAfter(nextNode, thisItem);
                            inserted = true;
                        }
                        else
                        {
                            nextNode = nextNode.Next;
                            nextItem = nextNode.Value;
                        }
                    }
                }
            }

            //================================================================================================//
            /// <summary>
            /// Insert section clearance item
            /// <\summary>

            public void InsertClearSection(float distance, int sectionIndex)
            {
                ClearSectionItem thisItem = new ClearSectionItem(distance, sectionIndex);
                InsertAction(thisItem);
            }

            //================================================================================================//
            /// <summary>
            /// Get list of items to be processed
            /// <\summary>

            public List<DistanceTravelledItem> GetActions(float distance)
            {
                List<DistanceTravelledItem> itemList = new List<DistanceTravelledItem>();

                bool itemsCollected = false;
                LinkedListNode<DistanceTravelledItem> nextNode = this.First;
                LinkedListNode<DistanceTravelledItem> prevNode;

                while (!itemsCollected && nextNode != null)
                {
                    if (nextNode.Value.RequiredDistance <= distance)
                    {
                        itemList.Add(nextNode.Value);
                        prevNode = nextNode;
                        nextNode = prevNode.Next;
                        this.Remove(prevNode);
                    }
                    else
                    {
                        itemsCollected = true;
                    }
                }
                return (itemList);
            }

            public List<DistanceTravelledItem> GetAuxActions(Train thisTrain, float distance)
            {
                List<DistanceTravelledItem> itemList = new List<DistanceTravelledItem>();
                LinkedListNode<DistanceTravelledItem> nextNode = this.First;

                while (nextNode != null)
                {
                    if (nextNode.Value is AuxActionItem)
                    {
                        AuxActionItem item = nextNode.Value as AuxActionItem;
                        if (item.CanActivate(thisTrain, thisTrain.SpeedMpS, false))
                            itemList.Add(nextNode.Value);
                    }
                    nextNode = nextNode.Next;
                }
                return (itemList);
            }

            //================================================================================================//
            /// <summary>
            /// Get list of items to be processed of particular type
            /// <\summary>

            public List<DistanceTravelledItem> GetActions(float distance, Type reqType)
            {
                List<DistanceTravelledItem> itemList = new List<DistanceTravelledItem>();

                bool itemsCollected = false;
                LinkedListNode<DistanceTravelledItem> nextNode = this.First;
                LinkedListNode<DistanceTravelledItem> prevNode;

                while (!itemsCollected && nextNode != null)
                {
                    if (nextNode.Value.RequiredDistance <= distance)
                    {
                        if (nextNode.Value.GetType() == reqType)
                        {
                            itemList.Add(nextNode.Value);
                            prevNode = nextNode;
                            nextNode = prevNode.Next;
                            this.Remove(prevNode);
                        }
                        else
                        {
                            nextNode = nextNode.Next;
                        }
                    }
                    else
                    {
                        itemsCollected = true;
                    }
                }

                return (itemList);
            }

            //================================================================================================//
            /// <summary>
            /// Get distance of last track clearance item
            /// <\summary>

            public float? GetLastClearingDistance()
            {
                float? lastDistance = null;

                bool itemsCollected = false;
                LinkedListNode<DistanceTravelledItem> nextNode = this.Last;

                while (!itemsCollected && nextNode != null)
                {
                    if (nextNode.Value is ClearSectionItem)
                    {
                        lastDistance = nextNode.Value.RequiredDistance;
                        itemsCollected = true;
                    }
                    nextNode = nextNode.Previous;
                }

                return (lastDistance);
            }

            //================================================================================================//
            /// <summary>
            /// update any pending speed limits to new limit
            /// <\summary>

            public void UpdatePendingSpeedlimits(float reqSpeedMpS)
            {
                foreach (var thisAction in this)
                {
                    if (thisAction is ActivateSpeedLimit)
                    {
                        ActivateSpeedLimit thisLimit = (thisAction as ActivateSpeedLimit);

                        if (thisLimit.MaxSpeedMpSLimit > reqSpeedMpS)
                        {
                            thisLimit.MaxSpeedMpSLimit = reqSpeedMpS;
                        }
                        if (thisLimit.MaxSpeedMpSSignal > reqSpeedMpS)
                        {
                            thisLimit.MaxSpeedMpSSignal = reqSpeedMpS;
                        }
                        if (thisLimit.MaxTempSpeedMpSLimit > reqSpeedMpS)
                        {
                            thisLimit.MaxTempSpeedMpSLimit = reqSpeedMpS;
                        }
                    }
                }
            }

            //================================================================================================//
            /// <summary>
            /// remove any pending AIActionItems
            /// <\summary>

            public void RemovePendingAIActionItems(bool removeAll)
            {
                List<DistanceTravelledItem> itemsToRemove = new List<DistanceTravelledItem>();

                foreach (var thisAction in this)
                {
                    if ((thisAction is AIActionItem && !(thisAction is AuxActionItem)) || removeAll)
                    {
                        DistanceTravelledItem thisItem = thisAction;
                        itemsToRemove.Add(thisItem);
                    }
                }

                foreach (var thisAction in itemsToRemove)
                {
                    this.Remove(thisAction);
                }

            }


            //================================================================================================//
            /// <summary>
            /// Modifies required distance of actions after a train coupling
            /// <\summary>

            public void ModifyRequiredDistance(float Length)
            {
                foreach (var thisAction in this)
                {
                    if (thisAction is DistanceTravelledItem)
                    {
                        (thisAction as DistanceTravelledItem).RequiredDistance += Length;
                    }
                }
            }
        }

        //================================================================================================//
        /// <summary>
        /// Distance Travelled action item - base class for all possible actions
        /// </summary>

        public class DistanceTravelledItem
        {
            public float RequiredDistance;

            //================================================================================================//
            //
            // Base contructor
            //

            public DistanceTravelledItem()
            {
            }

            //================================================================================================//
            //
            // Restore
            //

            public DistanceTravelledItem(BinaryReader inf)
            {
                RequiredDistance = inf.ReadSingle();
            }

            //================================================================================================//
            //
            // Save
            //

            public void Save(BinaryWriter outf)
            {
                if (this is ActivateSpeedLimit)
                {
                    outf.Write(1);
                    outf.Write(RequiredDistance);
                    ActivateSpeedLimit thisLimit = this as ActivateSpeedLimit;
                    thisLimit.SaveItem(outf);
                }
                else if (this is ClearSectionItem)
                {
                    outf.Write(2);
                    outf.Write(RequiredDistance);
                    ClearSectionItem thisSection = this as ClearSectionItem;
                    thisSection.SaveItem(outf);
                }
                else if (this is AIActionItem && !(this is AuxActionItem))
                {
                    outf.Write(3);
                    outf.Write(RequiredDistance);
                    AIActionItem thisAction = this as AIActionItem;
                    thisAction.SaveItem(outf);
                }
                else if (this is AuxActionItem)
                {
                    outf.Write(4);
                    outf.Write(RequiredDistance);
                    AuxActionItem thisAction = this as AuxActionItem;
                    thisAction.SaveItem(outf);
                }
                else
                {
                    outf.Write(-1);
                }

            }
        }

        //================================================================================================//
        /// <summary>
        /// Distance Travelled Clear Section action item
        /// </summary>

        public class ClearSectionItem : DistanceTravelledItem
        {
            public int TrackSectionIndex;  // in case of CLEAR_SECTION  //

            //================================================================================================//
            /// <summary>
            /// constructor for clear section
            /// </summary>

            public ClearSectionItem(float distance, int sectionIndex)
            {
                RequiredDistance = distance;
                TrackSectionIndex = sectionIndex;
            }

            //================================================================================================//
            //
            // Restore
            //

            public ClearSectionItem(BinaryReader inf)
                : base(inf)
            {
                TrackSectionIndex = inf.ReadInt32();
            }

            //================================================================================================//
            //
            // Save
            //

            public void SaveItem(BinaryWriter outf)
            {
                outf.Write(TrackSectionIndex);
            }


        }

        //================================================================================================//
        /// <summary>
        /// Distance Travelled Speed Limit Item
        /// </summary>

        public class ActivateSpeedLimit : DistanceTravelledItem
        {
            public float MaxSpeedMpSLimit = -1;
            public float MaxSpeedMpSSignal = -1;
            public float MaxTempSpeedMpSLimit = -1;

            //================================================================================================//
            /// <summary>
            /// constructor for speedlimit value
            /// </summary>

            public ActivateSpeedLimit(float reqDistance, float maxSpeedMpSLimit, float maxSpeedMpSSignal, float maxTempSpeedMpSLimit = -1)
            {
                RequiredDistance = reqDistance;
                MaxSpeedMpSLimit = maxSpeedMpSLimit;
                MaxSpeedMpSSignal = maxSpeedMpSSignal;
                MaxTempSpeedMpSLimit = maxTempSpeedMpSLimit;
            }

            //================================================================================================//
            //
            // Restore
            //

            public ActivateSpeedLimit(BinaryReader inf)
                : base(inf)
            {
                MaxSpeedMpSLimit = inf.ReadSingle();
                MaxSpeedMpSSignal = inf.ReadSingle();
                MaxTempSpeedMpSLimit = inf.ReadSingle();
            }

            //================================================================================================//
            //
            // Save
            //

            public void SaveItem(BinaryWriter outf)
            {
                outf.Write(MaxSpeedMpSLimit);
                outf.Write(MaxSpeedMpSSignal);
                outf.Write(MaxTempSpeedMpSLimit);
            }

        }

        //================================================================================================//
        /// <summary>
        /// StationStop class
        /// Class to hold information on station stops
        /// <\summary>

        public class StationStop : IComparable<StationStop>
        {

            public enum STOPTYPE
            {
                STATION_STOP,
                SIDING_STOP,
                MANUAL_STOP,
                WAITING_POINT,
            }

            // common variables
            public STOPTYPE ActualStopType;

            public int PlatformReference;
            public PlatformDetails PlatformItem;
            public int SubrouteIndex;
            public int RouteIndex;
            public int TCSectionIndex;
            public int Direction;
            public int ExitSignal;
            public bool HoldSignal;
            public bool NoWaitSignal;
            public bool CallOnAllowed;
            public bool NoClaimAllowed;
            public float StopOffset;
            public float DistanceToTrainM;
            public int ArrivalTime;
            public int DepartTime;
            public int ActualArrival;
            public int ActualDepart;
            public DateTime arrivalDT;
            public DateTime departureDT;
            public bool Passed;

            // variables for activity mode only
            public const int NumSecPerPass = 10; // number of seconds to board of a passengers
            public const int DefaultFreightStopTime = 20; // MSTS stoptime for freight trains

            // variables for timetable mode only
            public bool Terminal;                                                                 // station is terminal - train will run to end of platform
            public int? ActualMinStopTime;                                                        // actual minimum stop time
            public float? KeepClearFront = null;                                                  // distance to be kept clear ahead of train
            public float? KeepClearRear = null;                                                   // distance to be kept clear behind train
            public bool ForcePosition = false;                                                    // front or rear clear position must be forced
            public bool CloseupSignal = false;                                                    // train may close up to signal within normal clearing distance
            public bool RestrictPlatformToSignal = false;                                         // restrict end of platform to signal position
            public bool ExtendPlatformToSignal = false;                                           // extend end of platform to next signal position
            public bool EndStop = false;                                                          // train terminates at station
            public List<int> ConnectionsWaiting = new List<int>();                                // List of trains waiting
            public Dictionary<int, int> ConnectionsAwaited = new Dictionary<int, int>();          // List of awaited trains : key = trainno., value = arr time
            public Dictionary<int, WaitInfo> ConnectionDetails = new Dictionary<int, WaitInfo>(); // Details of connection : key = trainno., value = wait info

            //================================================================================================//
            //
            // Constructor
            //

            public StationStop(int platformReference, PlatformDetails platformItem, int subrouteIndex, int routeIndex,
                int tcSectionIndex, int direction, int exitSignal, bool holdSignal, bool noWaitSignal, bool noClaimAllowed, float stopOffset,
                int arrivalTime, int departTime, bool terminal, int? actualMinStopTime, float? keepClearFront, float? keepClearRear, bool forcePosition, bool closeupSignal,
                bool restrictPlatformToSignal, bool extendPlatformToSignal, bool endStop, STOPTYPE actualStopType)
            {
                ActualStopType = actualStopType;
                PlatformReference = platformReference;
                PlatformItem = platformItem;
                SubrouteIndex = subrouteIndex;
                RouteIndex = routeIndex;
                TCSectionIndex = tcSectionIndex;
                Direction = direction;
                ExitSignal = exitSignal;
                HoldSignal = holdSignal;
                NoWaitSignal = noWaitSignal;
                NoClaimAllowed = noClaimAllowed;
                StopOffset = stopOffset;
                if (actualStopType == STOPTYPE.STATION_STOP)
                {
                    ArrivalTime = Math.Max(0, arrivalTime);
                    DepartTime = Math.Max(0, departTime);
                }
                else
                // times may be <0 for waiting point
                {
                    ArrivalTime = arrivalTime;
                    DepartTime = departTime;
                }
                ActualArrival = -1;
                ActualDepart = -1;
                DistanceToTrainM = 9999999f;
                Passed = false;

                Terminal = terminal;
                ActualMinStopTime = actualMinStopTime;
                KeepClearFront = keepClearFront;
                KeepClearRear = keepClearRear;
                ForcePosition = forcePosition;
                CloseupSignal = closeupSignal;
                RestrictPlatformToSignal = restrictPlatformToSignal;
                ExtendPlatformToSignal = extendPlatformToSignal;
                EndStop = endStop;

                CallOnAllowed = false;
            }

            //================================================================================================//
            //
            // Constructor to create empty item (used for passing variables only)
            //

            public StationStop()
            {
            }

            //================================================================================================//
            //
            // Restore
            //

            public StationStop(BinaryReader inf, Signals signalRef)
            {
                ActualStopType = (STOPTYPE)inf.ReadInt32();
                PlatformReference = inf.ReadInt32();

                if (PlatformReference >= 0)
                {
                    int platformIndex;
                    if (signalRef.PlatformXRefList.TryGetValue(PlatformReference, out platformIndex))
                    {
                        PlatformItem = signalRef.PlatformDetailsList[platformIndex];
                    }
                    else
                    {
                        Trace.TraceInformation("Cannot find platform {0}", PlatformReference);
                    }
                }
                else
                {
                    PlatformItem = null;
                }

                SubrouteIndex = inf.ReadInt32();
                RouteIndex = inf.ReadInt32();
                TCSectionIndex = inf.ReadInt32();
                Direction = inf.ReadInt32();
                ExitSignal = inf.ReadInt32();
                HoldSignal = inf.ReadBoolean();
                NoWaitSignal = inf.ReadBoolean();
                NoClaimAllowed = inf.ReadBoolean();
                CallOnAllowed = inf.ReadBoolean();
                StopOffset = inf.ReadSingle();
                ArrivalTime = inf.ReadInt32();
                DepartTime = inf.ReadInt32();
                ActualArrival = inf.ReadInt32();
                ActualDepart = inf.ReadInt32();
                DistanceToTrainM = 9999999f;
                Passed = inf.ReadBoolean();
                arrivalDT = new DateTime(inf.ReadInt64());
                departureDT = new DateTime(inf.ReadInt64());

                ConnectionsWaiting = new List<int>();
                int totalConWait = inf.ReadInt32();
                for (int iCW = 0; iCW <= totalConWait - 1; iCW++)
                {
                    ConnectionsWaiting.Add(inf.ReadInt32());
                }

                ConnectionsAwaited = new Dictionary<int, int>();
                int totalConAwait = inf.ReadInt32();
                for (int iCA = 0; iCA <= totalConAwait - 1; iCA++)
                {
                    ConnectionsAwaited.Add(inf.ReadInt32(), inf.ReadInt32());
                }

                ConnectionDetails = new Dictionary<int, WaitInfo>();
                int totalConDetails = inf.ReadInt32();
                for (int iCD = 0; iCD <= totalConDetails - 1; iCD++)
                {
                    ConnectionDetails.Add(inf.ReadInt32(), new WaitInfo(inf));
                }

                if (inf.ReadBoolean())
                {
                    ActualMinStopTime = inf.ReadInt32();
                }
                else
                {
                    ActualMinStopTime = null;
                }

                if (inf.ReadBoolean())
                {
                    KeepClearFront = inf.ReadSingle();
                }
                else
                {
                    KeepClearFront = null;
                }

                if (inf.ReadBoolean())
                {
                    KeepClearRear = inf.ReadSingle();
                }
                else
                {
                    KeepClearRear = null;
                }

                Terminal = inf.ReadBoolean();
                ForcePosition = inf.ReadBoolean();
                CloseupSignal = inf.ReadBoolean();
                RestrictPlatformToSignal = inf.ReadBoolean();
                ExtendPlatformToSignal = inf.ReadBoolean();
                EndStop = inf.ReadBoolean();
            }

            //================================================================================================//
            //
            // Compare To (to allow sort)
            //

            public int CompareTo(StationStop otherStop)
            {
                if (this.SubrouteIndex < otherStop.SubrouteIndex)
                {
                    return -1;
                }
                else if (this.SubrouteIndex > otherStop.SubrouteIndex)
                {
                    return 1;
                }
                else if (this.RouteIndex < otherStop.RouteIndex)
                {
                    return -1;
                }
                else if (this.RouteIndex > otherStop.RouteIndex)
                {
                    return 1;
                }
                else if (this.StopOffset < otherStop.StopOffset)
                {
                    return -1;
                }
                else if (this.StopOffset > otherStop.StopOffset)
                {
                    return 1;
                }
                else
                {
                    return 0;
                }
            }

            //================================================================================================//
            //
            // Save
            //

            public void Save(BinaryWriter outf)
            {
                outf.Write((int)ActualStopType);
                outf.Write(PlatformReference);
                outf.Write(SubrouteIndex);
                outf.Write(RouteIndex);
                outf.Write(TCSectionIndex);
                outf.Write(Direction);
                outf.Write(ExitSignal);
                outf.Write(HoldSignal);
                outf.Write(NoWaitSignal);
                outf.Write(NoClaimAllowed);
                outf.Write(CallOnAllowed);
                outf.Write(StopOffset);
                outf.Write(ArrivalTime);
                outf.Write(DepartTime);
                outf.Write(ActualArrival);
                outf.Write(ActualDepart);
                outf.Write(Passed);
                outf.Write((Int64)arrivalDT.Ticks);
                outf.Write((Int64)departureDT.Ticks);

                outf.Write(ConnectionsWaiting.Count);
                foreach (int iWait in ConnectionsWaiting)
                {
                    outf.Write(iWait);
                }

                outf.Write(ConnectionsAwaited.Count);
                foreach (KeyValuePair<int, int> thisAwait in ConnectionsAwaited)
                {
                    outf.Write(thisAwait.Key);
                    outf.Write(thisAwait.Value);
                }

                outf.Write(ConnectionDetails.Count);
                foreach (KeyValuePair<int, WaitInfo> thisDetails in ConnectionDetails)
                {
                    outf.Write(thisDetails.Key);
                    WaitInfo thisWait = (WaitInfo)thisDetails.Value;
                    thisWait.Save(outf);
                }

                if ( ActualMinStopTime.HasValue)
                {
                    outf.Write(true);
                    outf.Write(ActualMinStopTime.Value);
                }
                else
                {
                    outf.Write(false);
                }

                if (KeepClearFront.HasValue)
                {
                    outf.Write(true);
                    outf.Write(KeepClearFront.Value);
                }
                else
                {
                    outf.Write(false);
                }
                if (KeepClearRear.HasValue)
                {
                    outf.Write(true);
                    outf.Write(KeepClearRear.Value);
                }
                else
                {
                    outf.Write(false);
                }

                outf.Write(Terminal);
                outf.Write(ForcePosition);
                outf.Write(CloseupSignal);
                outf.Write(RestrictPlatformToSignal);
                outf.Write(ExtendPlatformToSignal);
                outf.Write(EndStop);
            }

            /// <summary>
            ///  create copy
            /// </summary>
            /// <returns></returns>
            public StationStop CreateCopy()
            {
                return ((StationStop)this.MemberwiseClone());
            }

            /// <summary>
            /// Calculate actual depart time
            /// Make special checks for stops arount midnight
            /// </summary>
            /// <param name="presentTime"></param>

            public int CalculateDepartTime(int presentTime, Train stoppedTrain)
            {
                int eightHundredHours = 8 * 3600;
                int sixteenHundredHours = 16 * 3600;

                // preset depart to booked time
                ActualDepart = DepartTime;

                // correct arrival for stop around midnight
                if (ActualArrival < eightHundredHours && ArrivalTime > sixteenHundredHours) // arrived after midnight, expected to arrive before
                {
                    ActualArrival += (24 * 3600);
                }
                else if (ActualArrival > sixteenHundredHours && ArrivalTime < eightHundredHours) // arrived before midnight, expected to arrive before
                {
                    ActualArrival -= (24 * 3600);
                }

                // correct stop time for stop around midnight
                int stopTime = DepartTime - ArrivalTime;
                if (DepartTime < eightHundredHours && ArrivalTime > sixteenHundredHours) // stop over midnight
                {
                    stopTime += (24 * 3600);
                }

                // compute boarding time (depends on train type)
                var validSched = stoppedTrain.ComputeTrainBoardingTime(this, ref stopTime);

                // correct departure time for stop around midnight
                int correctedTime = ActualArrival + stopTime;
                if (validSched)
                {
                    ActualDepart = CompareTimes.LatestTime(DepartTime, correctedTime);
                }
                else
                {
                    ActualDepart = correctedTime;
                    if (ActualDepart < 0)
                    {
                        ActualDepart += (24 * 3600);
                        ActualArrival += (24 * 3600);
                    }
                }
                if (ActualDepart != correctedTime)
                {
                    stopTime += ActualDepart - correctedTime;
                    if (stopTime > 24 * 3600) stopTime -= 24 * 3600;
                    else if (stopTime < 0) stopTime += 24 * 3600;

                }
                return stopTime;
            }

            //================================================================================================//
            /// <summary>
            /// <CScomment> Compute boarding time for passenger train. Solution based on number of carriages within platform.
            /// Number of carriages computed considering an average Traincar length...
            /// ...moreover considering that carriages are in the train part within platform (MSTS apparently does so).
            /// Player train has more sophisticated computing, as in MSTS.
            /// As of now the position of the carriages within the train is computed here at every station together with the statement if the carriage is within the 
            /// platform boundaries. To be evaluated if the position of the carriages within the train could later be computed together with the CheckFreight method</CScomment>
            /// <\summary>
            public int ComputeStationBoardingTime(Train stopTrain)
            {
                var passengerCarsWithinPlatform = stopTrain.PassengerCarsNumber;
                int stopTime = DefaultFreightStopTime;
                if (passengerCarsWithinPlatform == 0) return stopTime; // pure freight train
                var distancePlatformHeadtoTrainHead = -stopTrain.StationStops[0].StopOffset
                   + PlatformItem.TCOffset[1, stopTrain.StationStops[0].Direction]
                   + stopTrain.StationStops[0].DistanceToTrainM;
                var trainPartOutsidePlatformForward = distancePlatformHeadtoTrainHead < 0 ? -distancePlatformHeadtoTrainHead : 0;
                if (trainPartOutsidePlatformForward >= stopTrain.Length) return (int)PlatformItem.MinWaitingTime; // train actually passed platform; should not happen
                var distancePlatformTailtoTrainTail = distancePlatformHeadtoTrainHead - PlatformItem.Length + stopTrain.Length;
                var trainPartOutsidePlatformBackward = distancePlatformTailtoTrainTail > 0 ? distancePlatformTailtoTrainTail : 0;
                if (trainPartOutsidePlatformBackward >= stopTrain.Length) return (int)PlatformItem.MinWaitingTime; // train actually stopped before platform; should not happen
                if (stopTrain == stopTrain.Simulator.OriginalPlayerTrain)
                {
                    if (trainPartOutsidePlatformForward == 0 && trainPartOutsidePlatformBackward == 0) passengerCarsWithinPlatform = stopTrain.PassengerCarsNumber;
                    else
                    {
                        if (trainPartOutsidePlatformForward > 0)
                        {
                            var walkingDistance = 0.0f;
                            int trainCarIndex = 0;
                            while (walkingDistance <= trainPartOutsidePlatformForward && passengerCarsWithinPlatform > 0 && trainCarIndex < stopTrain.Cars.Count - 1)
                            {
                                var walkingDistanceBehind = walkingDistance + stopTrain.Cars[trainCarIndex].CarLengthM;
                                if ((stopTrain.Cars[trainCarIndex].WagonType != TrainCar.WagonTypes.Freight && stopTrain.Cars[trainCarIndex].WagonType != TrainCar.WagonTypes.Tender && !stopTrain.Cars[trainCarIndex].IsDriveable) ||
                                   (stopTrain.Cars[trainCarIndex].IsDriveable && stopTrain.Cars[trainCarIndex].HasPassengerCapacity))
                                {
                                    if ((trainPartOutsidePlatformForward - walkingDistance) > 0.67 * stopTrain.Cars[trainCarIndex].CarLengthM) passengerCarsWithinPlatform--;
                                }
                                walkingDistance = walkingDistanceBehind;
                                trainCarIndex++;
                            }
                        }
                        if (trainPartOutsidePlatformBackward > 0 && passengerCarsWithinPlatform > 0)
                        {
                            var walkingDistance = 0.0f;
                            int trainCarIndex = stopTrain.Cars.Count - 1;
                            while (walkingDistance <= trainPartOutsidePlatformBackward && passengerCarsWithinPlatform > 0 && trainCarIndex >= 0)
                            {
                                var walkingDistanceBehind = walkingDistance + stopTrain.Cars[trainCarIndex].CarLengthM;
                                if ((stopTrain.Cars[trainCarIndex].WagonType != TrainCar.WagonTypes.Freight && stopTrain.Cars[trainCarIndex].WagonType != TrainCar.WagonTypes.Tender && !stopTrain.Cars[trainCarIndex].IsDriveable) ||
                                   (stopTrain.Cars[trainCarIndex].IsDriveable && stopTrain.Cars[trainCarIndex].HasPassengerCapacity))
                                {
                                    if ((trainPartOutsidePlatformBackward - walkingDistance) > 0.67 * stopTrain.Cars[trainCarIndex].CarLengthM) passengerCarsWithinPlatform--;
                                }
                                walkingDistance = walkingDistanceBehind;
                                trainCarIndex--;
                            }
                        }
                    }
                }
                else
                {

                    passengerCarsWithinPlatform = stopTrain.Length - trainPartOutsidePlatformForward - trainPartOutsidePlatformBackward > 0 ?
                    stopTrain.PassengerCarsNumber : (int)Math.Min((stopTrain.Length - trainPartOutsidePlatformForward - trainPartOutsidePlatformBackward) / stopTrain.Cars.Count() + 0.33,
                    stopTrain.PassengerCarsNumber);
                }
                if (passengerCarsWithinPlatform > 0)
                {
                    var actualNumPassengersWaiting = PlatformItem.NumPassengersWaiting;
                    if (stopTrain.TrainType != TRAINTYPE.AI_PLAYERHOSTING) RandomizePassengersWaiting(ref actualNumPassengersWaiting, stopTrain);
                    stopTime = Math.Max(NumSecPerPass * actualNumPassengersWaiting / passengerCarsWithinPlatform, DefaultFreightStopTime);
                }
                else stopTime = 0; // no passenger car stopped within platform: sorry, no countdown starts
                return stopTime;
            }

            //================================================================================================//
            /// <summary>
            /// CheckScheduleValidity
            /// Quite frequently in MSTS activities AI trains have invalid values (often near midnight), because MSTS does not consider them anyway
            /// As OR considers them, it is wise to discard the least credible values, to avoid AI trains stopping for hours
            /// </summary>
            public bool CheckScheduleValidity(Train stopTrain)
            {
                if (stopTrain.TrainType != Train.TRAINTYPE.AI) return true;
                if (ArrivalTime == DepartTime && Math.Abs(ArrivalTime - ActualArrival) > 14400) return false;
                else return true;
            }

            //================================================================================================//
            /// <summary>
            /// RandomizePassengersWaiting
            /// Randomizes number of passengers waiting for train, and therefore boarding time
            /// Randomization can be upwards or downwards
            /// </summary>

            private void RandomizePassengersWaiting (ref int actualNumPassengersWaiting, Train stopTrain)
            {
                if (stopTrain.Simulator.Settings.ActRandomizationLevel > 0)
                {
                    var randms = DateTime.Now.Millisecond % 10;
                    if (randms >= 6 - stopTrain.Simulator.Settings.ActRandomizationLevel)
                    {
                        if (randms < 8)
                        {
                            actualNumPassengersWaiting += stopTrain.RandomizedDelay(2 * PlatformItem.NumPassengersWaiting *
                                stopTrain.Simulator.Settings.ActRandomizationLevel); // real passenger number may be up to 3 times the standard.
                        }
                        else
                        // less passengers than standard
                        {
                            actualNumPassengersWaiting -= stopTrain.RandomizedDelay(PlatformItem.NumPassengersWaiting *
                                stopTrain.Simulator.Settings.ActRandomizationLevel / 6);
                        }
                    }
                }
            }

        }

        //================================================================================================//
        /// <summary>
        /// Class for info to TrackMonitor display
        /// <\summary>

        public class TrainInfo
        {
            public TRAIN_CONTROL ControlMode;                // present control mode 
            public float speedMpS;                           // present speed
            public float projectedSpeedMpS;                  // projected speed
            public float allowedSpeedMpS;                    // max allowed speed
            public float currentElevationPercent;            // elevation %
            public int direction;                            // present direction (0=forward, 1=backward)
            public int cabOrientation;                       // present cab orientation (0=forward, 1=backward)
            public bool isOnPath;                            // train is on defined path (valid in Manual mode only)
            public List<TrainObjectItem> ObjectInfoForward;  // forward objects
            public List<TrainObjectItem> ObjectInfoBackward; // backward objects

            //================================================================================================//
            /// <summary>
            /// Constructor - creates empty objects, data is filled by GetInfo routine from Train
            /// <\summary>

            public TrainInfo()
            {
                ObjectInfoForward = new List<TrainObjectItem>();
                ObjectInfoBackward = new List<TrainObjectItem>();
            }

            /// no need for Restore or Save items as info is not kept in permanent variables

        }

        //================================================================================================//
        /// <summary>
        /// Class TrainObjectItem : info on objects etc. in train path
        /// Used as interface for TrackMonitorWindow as part of TrainInfo class
        /// <\summary>

        public class TrainObjectItem : IComparable<TrainObjectItem>
        {
            public enum TRAINOBJECTTYPE
            {
                SIGNAL,
                SPEEDPOST,
                STATION,
                AUTHORITY,
                REVERSAL,
                OUT_OF_CONTROL,
                WAITING_POINT,
                MILEPOST,
                FACING_SWITCH
            }

            public enum SpeedItemType
            {
                Standard = 0,
                TempRestrictedStart = 1,
                TempRestrictedResume = 2,
            }

            public TRAINOBJECTTYPE ItemType;
            public OUTOFCONTROL OutOfControlReason;
            public END_AUTHORITY AuthorityType;
            public TrackMonitorSignalAspect SignalState;
            public float AllowedSpeedMpS;
            public float DistanceToTrainM;
            public bool Enabled;
            public int StationPlatformLength;
            public SpeedItemType SpeedObjectType;
            public bool Valid;
            public string ThisMile;
            public bool IsRightSwitch;

            // field validity :
            // if ItemType == SIGNAL :
            //      SignalState
            //      AllowedSpeedMpS if value > 0
            //      DistanceToTrainM
            //
            // if ItemType == SPEEDPOST :
            //      AllowedSpeedMpS
            //      DistanceToTrainM
            //
            // if ItemType == STATION :
            //      DistanceToTrainM
            //
            // if ItemType == AUTHORITY :
            //      AuthorityType
            //      DistanceToTrainM
            //
            // if ItemType == REVERSAL :
            //      DistanceToTrainM
            //
            // if ItemType == OUTOFCONTROL :
            //      OutOfControlReason


            //================================================================================================//
            /// <summary>
            /// Constructors
            /// <\summary>

            // Constructor for Signal
            public TrainObjectItem(TrackMonitorSignalAspect thisAspect, float thisSpeedMpS, float? thisDistanceM)
            {
                ItemType = TRAINOBJECTTYPE.SIGNAL;
                AuthorityType = END_AUTHORITY.NO_PATH_RESERVED;
                SignalState = thisAspect;
                AllowedSpeedMpS = thisSpeedMpS;
                DistanceToTrainM = thisDistanceM.HasValue ? thisDistanceM.Value : 0.1f;
            }

            // Constructor for Speedpost
            public TrainObjectItem(float thisSpeedMpS, float thisDistanceM, SpeedItemType speedObjectType = SpeedItemType.Standard)
            {
                ItemType = TRAINOBJECTTYPE.SPEEDPOST;
                AuthorityType = END_AUTHORITY.NO_PATH_RESERVED;
                SignalState = TrackMonitorSignalAspect.Clear_2;
                AllowedSpeedMpS = thisSpeedMpS;
                DistanceToTrainM = thisDistanceM;
                SpeedObjectType = speedObjectType;
            }

            // Constructor for Station
            public TrainObjectItem(float thisDistanceM, int thisPlatformLength)
            {
                ItemType = TRAINOBJECTTYPE.STATION;
                AuthorityType = END_AUTHORITY.NO_PATH_RESERVED;
                SignalState = TrackMonitorSignalAspect.Clear_2;
                AllowedSpeedMpS = -1;
                DistanceToTrainM = thisDistanceM;
                StationPlatformLength = thisPlatformLength;
            }

            // Constructor for Reversal
            public TrainObjectItem(bool enabled, float thisDistanceM, bool valid = true)
            {
                ItemType = TRAINOBJECTTYPE.REVERSAL;
                AuthorityType = END_AUTHORITY.NO_PATH_RESERVED;
                SignalState = TrackMonitorSignalAspect.Clear_2;
                AllowedSpeedMpS = -1;
                DistanceToTrainM = thisDistanceM;
                Enabled = enabled;
                Valid = valid;
            }

            // Constructor for Authority
            public TrainObjectItem(END_AUTHORITY thisAuthority, float thisDistanceM)
            {
                ItemType = TRAINOBJECTTYPE.AUTHORITY;
                AuthorityType = thisAuthority;
                SignalState = TrackMonitorSignalAspect.Clear_2;
                AllowedSpeedMpS = -1;
                DistanceToTrainM = thisDistanceM;
            }

            // Constructor for OutOfControl
            public TrainObjectItem(OUTOFCONTROL thisReason)
            {
                ItemType = TRAINOBJECTTYPE.OUT_OF_CONTROL;
                OutOfControlReason = thisReason;
            }

            // Constructor for Waiting Point
            public TrainObjectItem(float thisDistanceM, bool enabled)
            {
                ItemType = TRAINOBJECTTYPE.WAITING_POINT;
                AuthorityType = END_AUTHORITY.NO_PATH_RESERVED;
                SignalState = TrackMonitorSignalAspect.Clear_2;
                AllowedSpeedMpS = -1;
                DistanceToTrainM = thisDistanceM;
                Enabled = enabled;
            }

            // Constructor for Milepost
            public TrainObjectItem(string thisMile, float thisDistanceM)
            {
                ItemType = TRAINOBJECTTYPE.MILEPOST;
                AuthorityType = END_AUTHORITY.NO_PATH_RESERVED;
                SignalState = TrackMonitorSignalAspect.Clear_2;
                AllowedSpeedMpS = -1;
                DistanceToTrainM = thisDistanceM;
                ThisMile = thisMile;
            }

            // Constructor for facing Switch
            public TrainObjectItem(bool isRightSwitch, float thisDistanceM)
            {
                ItemType = TRAINOBJECTTYPE.FACING_SWITCH;
                DistanceToTrainM = thisDistanceM;
                IsRightSwitch = isRightSwitch;
            }


            /// no need for Restore or Save items as info is not kept in permanent variables

            //================================================================================================//
            //
            // Compare To (to allow sort)
            //

            public int CompareTo(TrainObjectItem otherItem)
            {
                if (this.DistanceToTrainM < otherItem.DistanceToTrainM)
                    return (-1);
                if (this.DistanceToTrainM == otherItem.DistanceToTrainM)
                    return (0);
                return (1);
            }
        }

        //used by remote train to update location based on message received
        public int expectedTileX, expectedTileZ, expectedTracIndex, expectedDIr, expectedTDir;
        public float expectedX, expectedZ, expectedTravelled, expectedLength;
        public bool updateMSGReceived;
        public bool jumpRequested; // set when a train jump has been requested by the server (when player re-enters game in old position
        public bool doJump; // used in conjunction with above flag to manage thread safety
        public bool doReverseTrav; // reverse rear traveller in AI reversal points
        public int doReverseMU;

        public void ToDoUpdate(int tni, int tX, int tZ, float x, float z, float eT, float speed, int dir, int tDir, float len, bool reverseTrav = false,
            int reverseMU = 0)
        {
            SpeedMpS = speed;
            expectedTileX = tX;
            expectedTileZ = tZ;
            expectedX = x;
            expectedZ = z;
            expectedTravelled = eT;
            expectedTracIndex = tni;
            expectedDIr = dir;
            expectedTDir = tDir;
            expectedLength = len;
            if (reverseTrav)
            {
                doReverseTrav = true;
                doReverseMU = reverseMU;
            }
            updateMSGReceived = true;
        }

        private void UpdateCarSlack(float expectedLength)
        {
            if (Cars.Count <= 1) return;
            var staticLength = 0f;
            foreach (var car in Cars)
            {
                staticLength += car.CarLengthM;
            }
            staticLength = (expectedLength - staticLength) / (Cars.Count - 1);
            foreach (var car in Cars)//update slack for each car
            {
                car.CouplerSlackM = staticLength - car.GetCouplerZeroLengthM();
            }

        }
        public void UpdateRemoteTrainPos(float elapsedClockSeconds)
        {
            float newDistanceTravelledM = DistanceTravelledM;
 //           float xx = 0;

            if (updateMSGReceived)
            {
                updateMSGReceived = false;
                try
                {
                    targetSpeedMpS = SpeedMpS;
                    if (doReverseTrav)
                    {
                        doReverseTrav = false;
                        ReverseFormation(doReverseMU == 1? true : false);
                        UpdateCarSlack(expectedLength);//update car slack first
                        CalculatePositionOfCars(elapsedClockSeconds, SpeedMpS * elapsedClockSeconds);
                        newDistanceTravelledM = DistanceTravelledM + (SpeedMpS * elapsedClockSeconds);
                        this.MUDirection = (Direction)expectedDIr;
                    }
                    else
                    {
                        UpdateCarSlack(expectedLength);//update car slack first

                        var x = travelled + LastSpeedMpS * elapsedClockSeconds + (SpeedMpS - LastSpeedMpS) / 2 * elapsedClockSeconds;
                        //                    xx = x;
                        this.MUDirection = (Direction)expectedDIr;

                        if (Math.Abs(x - expectedTravelled) < 1 || Math.Abs(x - expectedTravelled) > 20)
                        {
                            CalculatePositionOfCars(elapsedClockSeconds, expectedTravelled - travelled);
                            newDistanceTravelledM = DistanceTravelledM + expectedTravelled - travelled;

                            //if something wrong with the switch
                            if (this.RearTDBTraveller.TrackNodeIndex != expectedTracIndex)
                            {
                                Traveller t = null;
                                if (expectedTracIndex <= 0)
                                {
                                    t = new Traveller(Simulator.TSectionDat, Simulator.TDB.TrackDB.TrackNodes, expectedTileX, expectedTileZ, expectedX, expectedZ, (Traveller.TravellerDirection)expectedTDir);
                                }
                                else
                                {
                                    t = new Traveller(Simulator.TSectionDat, Simulator.TDB.TrackDB.TrackNodes, Simulator.TDB.TrackDB.TrackNodes[expectedTracIndex], expectedTileX, expectedTileZ, expectedX, expectedZ, (Traveller.TravellerDirection)expectedTDir);
                                }
                                //move = SpeedMpS > 0 ? 0.001f : -0.001f;
                                this.travelled = expectedTravelled;
                                this.RearTDBTraveller = t;
                                CalculatePositionOfCars();

                            }
                        }
                        else//if the predicted location and reported location are similar, will try to increase/decrease the speed to bridge the gap in 1 second
                        {
                            SpeedMpS += (expectedTravelled - x) / 1;
                            CalculatePositionOfCars(elapsedClockSeconds, SpeedMpS * elapsedClockSeconds);
                            newDistanceTravelledM = DistanceTravelledM + (SpeedMpS * elapsedClockSeconds);
                        }
                        if (jumpRequested)
                        {
                            doJump = true;
                            jumpRequested = false;
                        }
                    }
                }
                catch (Exception)
                {
                }
                /*if (Math.Abs(requestedSpeed) < 0.00001 && Math.Abs(SpeedMpS) > 0.01) updateMSGReceived = true; //if requested is stop, but the current speed is still moving
                else*/

            }
            else//no message received, will move at the previous speed
            {
                CalculatePositionOfCars(elapsedClockSeconds, SpeedMpS * elapsedClockSeconds);
                newDistanceTravelledM = DistanceTravelledM + (SpeedMpS * elapsedClockSeconds);
            }

            //update speed for each car, so wheels will rotate
            foreach (TrainCar car in Cars)
            {
                if (car != null)
                {
                    car.SpeedMpS = SpeedMpS;
                    if (car.Flipped) car.SpeedMpS = -car.SpeedMpS;
                    car.AbsSpeedMpS = car.AbsSpeedMpS * (1 - elapsedClockSeconds ) + targetSpeedMpS * elapsedClockSeconds;
                    if (car.IsDriveable && car is MSTSWagon)
                    {
                        (car as MSTSWagon).WheelSpeedMpS = SpeedMpS;
                        if (car.AbsSpeedMpS > 0.5f)
                        {
                            if (car is MSTSElectricLocomotive)
                            {
                                (car as MSTSElectricLocomotive).Variable1 = 70;
                                (car as MSTSElectricLocomotive).Variable2 = 70;
                            }
                            else if (car is MSTSDieselLocomotive)
                            {
                                (car as MSTSDieselLocomotive).Variable1 = 0.7f;
                                (car as MSTSDieselLocomotive).Variable2 = 0.7f;
                            }
                            else if (car is MSTSSteamLocomotive)
                            {
                                (car as MSTSSteamLocomotive).Variable1 = car.AbsSpeedMpS / car.DriverWheelRadiusM / MathHelper.Pi * 5;
                                (car as MSTSSteamLocomotive).Variable2 = 0.7f;
                            }
                        }
                        else if (car is MSTSLocomotive)
                        {
                            (car as MSTSLocomotive).Variable1 = 0;
                            (car as MSTSLocomotive).Variable2 = 0;
                        }
                    }
#if INDIVIDUAL_CONTROL
                if (car is MSTSLocomotive && car.CarID.StartsWith(MPManager.GetUserName()))
                        {
                            car.Update(elapsedClockSeconds);
                        }
#endif  
                }
            }
//            Trace.TraceWarning("SpeedMpS {0}  LastSpeedMpS {1}  AbsSpeedMpS {2}  targetSpeedMpS {7} x {3}  expectedTravelled {4}  travelled {5}  newDistanceTravelledM {6}",
//                SpeedMpS, LastSpeedMpS, Cars[0].AbsSpeedMpS, xx, expectedTravelled, travelled, newDistanceTravelledM, targetSpeedMpS);
            LastSpeedMpS = SpeedMpS;
            DistanceTravelledM = newDistanceTravelledM;

            //Orient();
            return;

        }

        /// <summary>
        /// Nullify valid routes
        /// </summary>
        public void ClearValidRoutes()
        {

            if (ValidRoute[0] != null)
            {
                int listIndex = PresentPosition[0].RouteListIndex;
                signalRef.BreakDownRouteList(ValidRoute[0], listIndex, routedForward);
                ClearDeadlocks();
            }

            ValidRoute[0] = null;
            LastReservedSection[0] = -1;

            if (ValidRoute[1] != null)
            {
                int listIndex = PresentPosition[1].RouteListIndex;
                signalRef.BreakDownRouteList(ValidRoute[1], listIndex, routedBackward);
            }
            ValidRoute[1] = null;
            LastReservedSection[1] = -1;
        }

        /// <summary>
        /// Clears reserved sections (used after manual switching)
        /// </summary>
        public void ClearReservedSections()
        {

            if (ValidRoute[0] != null)
            {
                int listIndex = PresentPosition[0].RouteListIndex;
                signalRef.BreakDownRouteList(ValidRoute[0], listIndex, routedForward);
                ClearDeadlocks();
            }

        }


        /// <summary>
        /// After turntable rotation, must find where it is
        /// </summary>
        /// 
        public void ReenterTrackSections(int trackNodeIndex, int trVectorSectionIndex, Vector3 finalFrontTravellerXNALocation, Vector3 finalRearTravellerXNALocation, Traveller.TravellerDirection direction)
        {
            FrontTDBTraveller = new Traveller(Simulator.TSectionDat, Simulator.TDB.TrackDB.TrackNodes, Simulator.TDB.TrackDB.TrackNodes[trackNodeIndex],
                 Cars[0].WorldPosition.TileX, Cars[0].WorldPosition.TileZ, finalFrontTravellerXNALocation.X, -finalFrontTravellerXNALocation.Z, FrontTDBTraveller.Direction);
            RearTDBTraveller = new Traveller(Simulator.TSectionDat, Simulator.TDB.TrackDB.TrackNodes, Simulator.TDB.TrackDB.TrackNodes[trackNodeIndex],
                Cars[0].WorldPosition.TileX, Cars[0].WorldPosition.TileZ, finalRearTravellerXNALocation.X, -finalRearTravellerXNALocation.Z, RearTDBTraveller.Direction);
            if (direction == Traveller.TravellerDirection.Backward)
            {
                FrontTDBTraveller.ReverseDirection();
                RearTDBTraveller.ReverseDirection();
            }

            ClearValidRoutes();
            bool canPlace = true;
            PresentPosition[0].TCSectionIndex = -1;
            TCSubpathRoute tempRoute = CalculateInitialTrainPosition(ref canPlace);
            if (tempRoute.Count == 0 || !canPlace)
            {
                throw new InvalidDataException("Position of train in turntable not clear");
            }

            SetInitialTrainRoute(tempRoute);
            CalculatePositionOfCars();
            ResetInitialTrainRoute(tempRoute);

            CalculatePositionOfCars();

            TrackNode tn = FrontTDBTraveller.TN;
            float offset = FrontTDBTraveller.TrackNodeOffset;
            int direction1 = (int)FrontTDBTraveller.Direction;

            PresentPosition[0].SetTCPosition(tn.TCCrossReference, offset, direction1);
            PresentPosition[0].CopyTo(ref PreviousPosition[0]);

            if (TrainType == TRAINTYPE.STATIC)
            {
                ControlMode = TRAIN_CONTROL.UNDEFINED;
                return;
            }

            if (Simulator.Activity == null && !Simulator.TimetableMode) ToggleToExplorerMode();
            else ToggleToManualMode();
            Simulator.Confirmer.Confirm(CabControl.SignalMode, CabSetting.Off);
        }

    }// class Train
}
