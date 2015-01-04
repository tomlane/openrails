﻿// COPYRIGHT 2009, 2010, 2011, 2013 by the Open Rails project.
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


using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Globalization;
using GNU.Gettext;

namespace ORTS.Common
{
    // Classes are provided for converting into and out of these internal units.
    // OR will use metric units (m, kg, s, A, 'C) for internal properties and calculations, preferably from SI (m/s, not km/hr).
    // Use these classes rather than in-line literal factors.
    //
    // For example to convert a number from metres to inches, use "DiameterIn = M.ToIn(DiameterM);"
    // 
    // Many units begin with a lowercase letter (kg, kW, in, lb) but capitalised here (Kg, KW, In, Lb) for ease of reading.
    //
    // Web research suggests that VC++ will optimize "/ 2.0" replacing it with "* 0.5f" but VC# will not and cost is around 15 cycles.
    // To prevent this, we replace "/ 2.0f" by "(1.0f / 2.0f)", which will be replaced by "*0.5f" already in CIL code (verified in CIL).
    // This enables us to use the same number for both directions of conversion, while not costing any speed.
    //
    // Also because of performance reasons, derived quantities still are hard-coded, instead of calling basic conversions and do multiplication
    //
    // Note: this class has unit tests

    /// <summary>
    /// Enumerate the various units of pressure that are used
    /// </summary>
    public enum PressureUnit
    {
        /// <summary>non-defined unit</summary>
        None,
        /// <summary>kiloPascal</summary>
        KPa,
        /// <summary>bar</summary>
        Bar,
        /// <summary>Pounds Per Square Inch</summary>
        PSI,
        /// <summary>Inches Mercury</summary>
        InHg,
        /// <summary>Mass-force per square centimetres</summary>
        KgfpCm2
    }

    /// <summary>
    /// Distance conversions from and to metres
    /// </summary>
    public static class Me {   // Not M to avoid conflict with MSTSMath.M, but note that MSTSMath.M will be gone in future.
        /// <summary>Convert (statute or land) miles to metres</summary>
        public static float FromMi(float miles)  { return miles  * 1609.344f; }
        /// <summary>Convert metres to (statute or land) miles</summary>
        public static float ToMi(float metres)   { return metres * (1.0f / 1609.344f); }
        /// <summary>Convert kilometres to metres</summary>
        public static float FromKiloM(float miles) { return miles * 1000f; }
        /// <summary>Convert metres to kilometres</summary>
        public static float ToKiloM(float metres) { return metres * (1.0f / 1000f); }
        /// <summary>Convert yards to metres</summary>
        public static float FromYd(float yards)  { return yards  * 0.9144f; }
        /// <summary>Convert metres to yards</summary>
        public static float ToYd(float metres)   { return metres * (1.0f / 0.9144f); }
        /// <summary>Convert feet to metres</summary>
        public static float FromFt(float feet)   { return feet   * 0.3048f; }
        /// <summary>Convert metres to feet</summary>
        public static float ToFt(float metres)   { return metres *(1.0f/ 0.3048f); }
        /// <summary>Convert inches to metres</summary>
        public static float FromIn(float inches) { return inches * 0.0254f; }
        /// <summary>Convert metres to inches</summary>
        public static float ToIn(float metres)   { return metres * (1.0f / 0.0254f); }

        /// <summary>
        /// Convert from metres into kilometres or miles, depending on the flag isMetric
        /// </summary>
        /// <param name="distance">distance in metres</param>
        /// <param name="isMetric">if true convert to kilometres, if false convert to miles</param>
        public static float FromM(float distance, bool isMetric)
        {
            return isMetric ? ToKiloM(distance) : ToMi(distance);
        }
        /// <summary>
        /// Convert to metres from kilometres or miles, depending on the flag isMetric
        /// </summary>
        /// <param name="distance">distance to be converted to metres</param>
        /// <param name="isMetric">if true convert from kilometres, if false convert from miles</param>
        public static float ToM(float distance, bool isMetric)
        {
            return isMetric ? FromKiloM(distance) : FromMi(distance);
        }
    }


    /// <summary>
    /// Area conversions from and to m^2
    /// </summary>
    public static class Me2
    {
        /// <summary>Convert from feet squared to metres squared</summary>
        public static float FromFt2(float feet2) { return feet2   * 0.092903f; }
        /// <summary>Convert from metres squared to feet squared</summary>
        public static float ToFt2(float metres2) { return metres2 * (1.0f / 0.092903f); }
        /// <summary>Convert from inches squared to metres squared</summary>
        public static float FromIn2(float feet2) { return feet2   * (1.0f / 1550.0031f); }
        /// <summary>Convert from metres squared to inches squared</summary>
        public static float ToIn2(float metres2) { return metres2 * 1550.0031f; }
    }

    /// <summary>
    /// Volume conversions from and to m^3
    /// </summary>
    public static class Me3
    {
        /// <summary>Convert from cubic feet to cubic metres</summary>
        public static float FromFt3(float feet3) { return feet3   * (1.0f / 35.3146665722f); }
        /// <summary>Convert from cubic metres to cubic feet</summary>
        public static float ToFt3(float metres3) { return metres3 * 35.3146665722f; }
        /// <summary>Convert from cubic inches to cubic metres</summary>
        public static float FromIn3(float inches3) { return inches3 * (1.0f / 61023.7441f); }
        /// <summary>Convert from cubic metres to cubic inches</summary>
        public static float ToIn3(float metres3)   { return metres3 * 61023.7441f; }
    }

    /// <summary>
    /// Speed conversions from and to metres/sec
    /// </summary>
	public static class MpS
    {
        /// <summary>Convert miles/hour to metres/second</summary>
        public static float FromMpH(float milesPerHour)     { return milesPerHour   * (1.0f / 2.23693629f); }
        /// <summary>Convert metres/second to miles/hour</summary>
        public static float ToMpH(float metrePerSecond)     { return metrePerSecond * 2.23693629f; }
        /// <summary>Convert kilometre/hour to metres/second</summary>
        public static float FromKpH(float kilometrePerHour) { return kilometrePerHour * (1.0f / 3.600f); }
        /// <summary>Convert metres/second to kilometres/hour</summary>
        public static float ToKpH(float metrePerSecond)     { return metrePerSecond   * 3.600f; }
        
        /// <summary>
        /// Convert from metres/second to kilometres/hour or miles/hour, depending on value of isMetric
        /// </summary>
        /// <param name="speed">speed in metres/second</param>
        /// <param name="isMetric">true to convert to kilometre/hour, false to convert to miles/hour</param>
        public static float FromMpS(float speed, bool isMetric)
        {
            return isMetric ? ToKpH(speed) : ToMpH(speed);
        }

        /// <summary>
        /// Convert to metres/second from kilometres/hour or miles/hour, depending on value of isMetric
        /// </summary>
        /// <param name="speed">speed to be converted to metres/second</param>
        /// <param name="isMetric">true to convert from kilometre/hour, false to convert from miles/hour</param>
        public static float ToMpS(float speed, bool isMetric)
        {
            return isMetric ? FromKpH(speed) : FromMpH(speed);
        }
    }

    /// <summary>
    /// Mass conversions from and to Kilograms
    /// </summary>
    public static class Kg 
    {
        /// <summary>Convert from pounds (lb) to kilograms</summary>
        public static float FromLb(float lb)     { return lb * (1.0f / 2.20462f); }
        /// <summary>Convert from kilograms to pounds (lb)</summary>
        public static float ToLb(float kg)       { return kg * 2.20462f; }
        /// <summary>Convert from US Tons to kilograms</summary>
        public static float FromTUS(float tonsUS) { return tonsUS * 907.1847f; }
        /// <summary>Convert from kilograms to US Tons</summary>
        public static float ToTUS(float kg)       { return kg     * (1.0f / 907.1847f); }
        /// <summary>Convert from UK Tons to kilograms</summary>
        public static float FromTUK(float tonsUK) { return tonsUK * 1016.047f; }
        /// <summary>Convert from kilograms to UK Tons</summary>
        public static float ToTUK(float kg)       { return kg     * (1.0f / 1016.047f); }
        /// <summary>Convert from kilogram to metric tonnes</summary>
        public static float ToTonne(float kg)      { return kg    * (1.0f / 1000.0f); }
        /// <summary>Convert from metrix tonnes to kilogram</summary>
        public static float FromTonne(float tonne) { return tonne * 1000.0f; }
    }

    /// <summary>
    /// Force conversions from and to Newtons
    /// </summary>
    public static class N 
    {
        /// <summary>Convert from pound-force to Newtons</summary>
        public static float FromLbf(float lbf)  { return lbf    * (1.0f / 0.224808943871f); }
        /// <summary>Convert from Newtons to Pound-force</summary>
        public static float ToLbf(float newton) { return newton * 0.224808943871f; }
    }

    /// <summary>
    /// Mass rate conversions from and to Kg/s
    /// </summary>
    public static class KgpS
    {
        /// <summary>Convert from pound/hour to kilograms/second</summary>
        public static float FromLbpH(float poundsPerHour)    { return poundsPerHour      * (1.0f / 7936.64144f); }
        /// <summary>Convert from kilograms/second to pounds/hour</summary>
        public static float ToLbpH(float kilogramsPerSecond) { return kilogramsPerSecond * 7936.64144f; }
    }

    /// <summary>
    /// Power conversions from and to Watts
    /// </summary>
    public static class W 
    {
        /// <summary>Convert from kiloWatts to Watts</summary>
        public static float FromKW(float kiloWatts) { return kiloWatts * 1000f; }
        /// <summary>Convert from Watts to kileWatts</summary>
        public static float ToKW(float watts)       { return watts     * (1.0f / 1000f); }
        /// <summary>Convert from HorsePower to Watts</summary>
        public static float FromHp(float horsePowers) { return horsePowers * 745.699872f; }
        /// <summary>Convert from Watts to HorsePower</summary>
        public static float ToHp(float watts)         { return watts       * (1.0f / 745.699872f); }
        /// <summary>Convert from British Thermal Unit (BTU) per second to watts</summary>
        public static float FromBTUpS(float btuPerSecond) { return btuPerSecond * 1055.05585f; }
        /// <summary>Convert from Watts to British Thermal Unit (BTU) per second</summary>
        public static float ToBTUpS(float watts)          { return watts        * (1.0f / 1055.05585f); }
    }

    /// <summary>
    /// Stiffness conversions from and to Newtons/metre
    /// </summary>
    public static class NpM 
    {
    }

    /// <summary>
    /// Resistance conversions from and to Newtons/metre/sec
    /// </summary>
    public static class NpMpS
    {
    }

    /// <summary>
    /// Pressure conversions from and to kilopascals
    /// </summary>
    public static class KPa
    {
        /// <summary>Convert from Pounds per Square Inch to kiloPascal</summary>
        public static float FromPSI(float psi) { return psi * 6.89475729f; }
        /// <summary>Convert from kiloPascal to Pounds per Square Inch</summary>
        public static float ToPSI(float kiloPascal) { return kiloPascal * (1.0f / 6.89475729f); }
        /// <summary>Convert from Inches Mercury to kiloPascal</summary>
        public static float FromInHg(float inchesMercury) { return inchesMercury * 3.386389f; }
        /// <summary>Convert from kiloPascal to Inches Mercury</summary>
        public static float ToInHg(float kiloPascal) { return kiloPascal * (1.0f / 3.386389f); }
        /// <summary>Convert from Bar to kiloPascal</summary>
        public static float FromBar(float bar) { return bar * 100.0f; }
        /// <summary>Convert from kiloPascal to Bar</summary>
        public static float ToBar(float kiloPascal) { return kiloPascal * (1.0f / 100.0f); }
        /// <summary>Convert from mass-force per square metres to kiloPascal</summary>
        public static float FromKgfpCm2(float f) { return f * 98.068059f; }
        /// <summary>Convert from kiloPascal to mass-force per square centimetres</summary>
        public static float ToKgfpCm2(float kiloPascal) { return kiloPascal * (1.0f / 98.068059f); }

        /// <summary>
        /// Convert from KPa to any pressure unit
        /// </summary>
        /// <param name="pressure">pressure to convert from</param>
        /// <param name="outputUnit">Unit to convert To</param>
        public static float FromKPa(float pressure, PressureUnit outputUnit)
        {
            switch (outputUnit)
            {
                case PressureUnit.KPa:
                    return pressure;
                case PressureUnit.Bar:
                    return ToBar(pressure);
                case PressureUnit.InHg:
                    return ToInHg(pressure);
                case PressureUnit.KgfpCm2:
                    return ToKgfpCm2(pressure);
                case PressureUnit.PSI:
                    return ToPSI(pressure);
                default:
                    throw new ArgumentOutOfRangeException("Pressure unit not recognized");
            }
        }

        /// <summary>
        /// Convert from any pressure unit to KPa
        /// </summary>
        /// <param name="pressure">pressure to convert from</param>
        /// <param name="inputUnit">Unit to convert from</param>
        public static float ToKPa(float pressure, PressureUnit inputUnit)
        {
            switch (inputUnit)
            {
                case PressureUnit.KPa:
                    return pressure;
                case PressureUnit.Bar:
                    return FromBar(pressure);
                case PressureUnit.InHg:
                    return FromInHg(pressure);
                case PressureUnit.KgfpCm2:
                    return FromKgfpCm2(pressure);
                case PressureUnit.PSI:
                    return FromPSI(pressure);
                default:
                    throw new ArgumentOutOfRangeException("Pressure unit not recognized");
            }
        }
    }

    /// <summary>
    /// Pressure conversions from and to bar
    /// </summary>
    public static class Bar
    {
        /// <summary>Convert from kiloPascal to Bar</summary>
        public static float FromKPa(float kiloPascal) { return kiloPascal * (1.0f / 100.0f); }
        /// <summary>Convert from bar to kiloPascal</summary>
        public static float ToKPa(float bar) { return bar * 100.0f; }
        /// <summary>Convert from Pounds per Square Inch to Bar</summary>
        public static float FromPSI(float poundsPerSquareInch) { return poundsPerSquareInch * (1.0f / 14.5037738f); }
        /// <summary>Convert from Bar to Pounds per Square Inch</summary>
        public static float ToPSI(float bar) { return bar * 14.5037738f; }
        /// <summary>Convert from Inches Mercury to bar</summary>
        public static float FromInHg(float inchesMercury) { return inchesMercury * 0.03386389f; }
        /// <summary>Convert from bar to Inches Mercury</summary>
        public static float ToInHg(float bar) { return bar * (1.0f / 0.03386389f); }
        /// <summary>Convert from mass-force per square metres to bar</summary>
        public static float FromKgfpCm2(float f) { return f * (1.0f / 1.0197f); }
        /// <summary>Convert from bar to mass-force per square metres</summary>
        public static float ToKgfpCm2(float bar) { return bar * 1.0197f; }
    }

    /// <summary>
    /// Pressure rate conversions from and to bar/s
    /// </summary>
    public static class BarpS
    {
        /// <summary>Convert from Pounds per square Inch per second to bar per second</summary>
        public static float FromPSIpS(float psi) { return psi * (1.0f / 14.5037738f); }
        /// <summary>Convert from</summary>
        public static float ToPSIpS(float bar) { return bar * 14.5037738f; }
    }

    /// <summary>
    /// Energy density conversions from and to kJ/Kg
    /// </summary>
    public static class KJpKg
    {
        /// <summary>Convert from Britisch Thermal Units per Pound to kiloJoule per kilogram</summary>
        public static float FromBTUpLb(float btuPerPound) { return btuPerPound * 2.326f; }
        /// <summary>Convert from kiloJoule per kilogram to Britisch Thermal Units per Pound</summary>
        public static float ToBTUpLb(float kJPerkg) { return kJPerkg * (1.0f / 2.326f); }
    }

    /// <summary>
    /// Liquid volume conversions from and to Litres
    /// </summary>
    public static class L 
    {
        /// <summary>Convert from UK Gallons to litres</summary>
        public static float FromGUK(float gallonUK) { return gallonUK * 4.54609f; }
        /// <summary>Convert from litres to UK Gallons</summary>
        public static float ToGUK(float litre) { return litre * (1.0f / 4.54609f); }
        /// <summary>Convert from US Gallons to litres</summary>
        public static float FromGUS(float gallonUS) { return gallonUS * 3.78541f; }
        /// <summary>Convert from litres to US Gallons</summary>
        public static float ToGUS(float litre) { return litre * (1.0f / 3.78541f); }
    }

    /// <summary>
    /// Current conversions from and to Amps
    /// </summary>
    public static class A
    {
    }

    /// <summary>
    /// Frequency conversions from and to Hz (revolutions/sec)
    /// </summary>
    public static class pS 
    {
        /// <summary>Convert from per Minute to per Second</summary>
        public static float FrompM(float revPerMinute) { return revPerMinute * (1.0f / 60f); }
        /// <summary>Convert from per Second to per Minute</summary>
        public static float TopM(float revPerSecond) { return revPerSecond * 60f; }
        /// <summary>Convert from per Hour to per Second</summary>
        public static float FrompH(float revPerHour) { return revPerHour * (1.0f / 3600f); }
        /// <summary>Convert from per Second to per Hour</summary>
        public static float TopH(float revPerSecond) { return revPerSecond * 3600f; }
    }

    /// <summary>
    /// Time conversions from and to Seconds
    /// </summary>
    public static class S
    {
        /// <summary>Convert from minutes to seconds</summary>
        public static float FromM(float minutes) { return minutes * 60f; }
        /// <summary>Convert from seconds to minutes</summary>
        public static float ToM(float seconds) { return seconds * (1.0f / 60f); }
        /// <summary>Convert from hours to seconds</summary>
        public static float FromH(float hours) { return hours * 3600f; }
        /// <summary>Convert from seconds to hours</summary>
        public static float ToH(float seconds) { return seconds * (1.0f / 3600f); }
    }

    /// <summary>
    /// Temperature conversions from and to Celsius
    /// </summary>
    public static class C
    {
        /// <summary>Convert from degrees Fahrenheit to degrees Celcius</summary>
        public static float FromF(float fahrenheit) { return (fahrenheit - 32f) * (100f / 180f); }
        /// <summary>Convert from degrees Celcius to degrees Fahrenheit</summary>
        public static float ToF(float celcius) { return celcius * (180f / 100f) + 32f; }
        /// <summary>Convert from Kelving to degrees Celcius</summary>
        public static float FromK(float kelvin) { return kelvin - 273.15f; }
        /// <summary>Convert from degress Celcius to Kelvin</summary>
        public static float ToK(float celcius) { return celcius + 273.15f; }
    }

    /// <summary>
    /// Class to compare times taking into account times after midnight
    /// (morning comes after night comes after evening, but morning is before afternoon, which is before evening)
    /// </summary>
    public static class CompareTimes
    {
        static int eightHundredHours = 8 * 3600;
        static int sixteenHundredHours = 16 * 3600;

        /// <summary>
        /// Return the latest time of the two input times, keeping in mind that night/morning is after evening/night
        /// </summary>
        public static int LatestTime(int time1, int time2)
        {
            if (time1 > sixteenHundredHours && time2 < eightHundredHours)
            {
                return (time2);
            }
            else if (time1 < eightHundredHours && time2 > sixteenHundredHours)
            {
                return (time1);
            }
            else if (time1 > time2)
            {
                return (time1);
            }
            return (time2);
        }

        /// <summary>
        /// Return the Earliest time of the two input times, keeping in mind that night/morning is after evening/night
        /// </summary>
        public static int EarliestTime(int time1, int time2)
        {
            if (time1 > sixteenHundredHours && time2 < eightHundredHours)
            {
                return (time1);
            }
            else if (time1 < eightHundredHours && time2 > sixteenHundredHours)
            {
                return (time2);
            }
            else if (time1 > time2)
            {
                return (time2);
            }
            return (time1);
        }
    }


    /// <summary>
    /// Class to convert various quantities (so a value with a unit) into nicely formatted strings for display
    /// </summary>
    public static class FormatStrings
    {
        static GettextResourceManager Catalog = new GettextResourceManager("ORTS.Common");
        static string m = Catalog.GetString("m");
        static string km = Catalog.GetString("km");
        static string mi = Catalog.GetString("mi");
        static string ft = Catalog.GetString("ft");
        static string yd = Catalog.GetString("yd");
        static string kmph = Catalog.GetString("km/h");
        static string mph = Catalog.GetString("mph");
        static string kpa = Catalog.GetString("kPa");
        static string bar = Catalog.GetString("bar");
        static string psi = Catalog.GetString("psi");
        static string inhg = Catalog.GetString("inHg");
        static string kgfpcm2 = Catalog.GetString("kgf/cm^2");

        /// <summary>
        /// Formatted unlocalized speed string, used in reports and logs.
        /// </summary>
        public static string FormatSpeed(float speed, bool isMetric)
        {
            return String.Format(CultureInfo.CurrentCulture,
                "{0:F1}{1}", MpS.FromMpS(speed, isMetric), isMetric ? kmph : mph);
        }

        /// <summary>
        /// Formatted localized speed string, used to display tracking speed, with 1 decimal precision
        /// </summary>
        public static string FormatSpeedDisplay(float speed, bool isMetric)
        {
            return String.Format(CultureInfo.CurrentCulture,
                "{0:F1} {1}", MpS.FromMpS(speed, isMetric), isMetric ? kmph : mph);
        }

        /// <summary>
        /// Formatted localized speed string, used to display speed limits, with 0 decimal precision
        /// </summary>
        public static string FormatSpeedLimit(float speed, bool isMetric)
        {
            return String.Format(CultureInfo.CurrentCulture,
                "{0:F0} {1}", MpS.FromMpS(speed, isMetric), isMetric ? kmph : mph);
        }

        /// <summary>
        /// Formatted unlocalized distance string, used in reports and logs.
        /// </summary>
        public static string FormatDistance(float distance, bool isMetric)
        {
            if (isMetric)
            {
                // <0.1 kilometres, show metres.
                if (Math.Abs(distance) < 100)
                {
                    return String.Format(CultureInfo.CurrentCulture,
                        "{0:N0}m", distance);
                }
                return String.Format(CultureInfo.CurrentCulture,
                    "{0:F1}km", Me.ToKiloM(distance));
            }
            // <0.1 miles, show yards.
            if (Math.Abs(distance) < Me.FromMi(0.1f))
            {
                return String.Format(CultureInfo.CurrentCulture, "{0:N0}yd", Me.ToYd(distance));
            }
            return String.Format(CultureInfo.CurrentCulture, "{0:F1}mi", Me.ToMi(distance));
        }

        /// <summary>
        /// Formatted localized distance string, as displayed in in-game windows
        /// </summary>
        public static string FormatDistanceDisplay(float distance, bool isMetric)
        {
            if (isMetric)
            {
                // <0.1 kilometres, show metres.
                if (Math.Abs(distance) < 100)
                {
                    return String.Format(CultureInfo.CurrentCulture, "{0:N0} {1}", distance, m);
                }
                return String.Format(CultureInfo.CurrentCulture, "{0:F1} {1}", Me.ToKiloM(distance), km);
            }
            // <0.1 miles, show yards.
            if (Math.Abs(distance) < Me.FromMi(0.1f))
            {
                return String.Format(CultureInfo.CurrentCulture, "{0:N0} {1}", Me.ToYd(distance), yd);
            }
            return String.Format(CultureInfo.CurrentCulture, "{0:F1} {1}", Me.ToMi(distance), mi);
        }

        public static string FormatShortDistanceDisplay(float distanceM, bool isMetric)
        {
            if (isMetric)
                return String.Format(CultureInfo.CurrentCulture, "{0:N0} {1}", distanceM, m);
            return String.Format(CultureInfo.CurrentCulture, "{0:N0} {1}", Me.ToFt(distanceM), ft);
        }

        /// <summary>
        /// format localized mass string, as displayed in in-game windows.
        /// </summary>
        /// <param name="mass">mass in kg or in Lb</param>
        /// <param name="isMetric">use kg if true, Lb if false</param>
        public static string FormatMass(float mass, bool isMetric)
        {
            if (isMetric)
            {
                // < 1 tons, show kilograms.
                float massInTonne = Kg.ToTonne(mass);
                if (Math.Abs(massInTonne) > 1)
                {
                    return String.Format(CultureInfo.CurrentCulture, "{0:N0}t", massInTonne);
                }
                else
                {
                    return String.Format(CultureInfo.CurrentCulture, "{0:F1}kg", mass);
                }
            }
            else
            {
                return String.Format(CultureInfo.CurrentCulture,"{0:F1}Lb", Kg.ToLb(mass));
            }
        }

        /// <summary>
        /// Formatted localized pressure string
        /// </summary>
        public static string FormatPressure(float pressure, PressureUnit inputUnit, PressureUnit outputUnit, bool unitDisplayed)
        {
            if (inputUnit == PressureUnit.None || outputUnit == PressureUnit.None)
                return string.Empty;

            float pressureKPa = KPa.ToKPa(pressure, inputUnit);
            float pressureOut = KPa.FromKPa(pressureKPa, outputUnit);

            string unit = "";
            string format = "";
            switch (outputUnit)
            {
                case PressureUnit.KPa:
                    unit = kpa;
                    format = "{0:F0}";
                    break;

                case PressureUnit.Bar:
                    unit = bar;
                    format = "{0:F1}";
                    break;

                case PressureUnit.PSI:
                    unit = psi;
                    format = "{0:F0}";
                    break;

                case PressureUnit.InHg:
                    unit = inhg;
                    format = "{0:F0}";
                    break;

                case PressureUnit.KgfpCm2:
                    unit = kgfpcm2;
                    format = "{0:F1}";
                    break;
            }

            if (unitDisplayed)
            {
                format += " " + unit;
            }

            return String.Format(CultureInfo.CurrentCulture, format, pressureOut);
        }
    }

}
