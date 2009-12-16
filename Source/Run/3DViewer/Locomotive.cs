﻿/* LOCOMOTIVE CLASSES
 * 
 * Used a a base for Steam, Diesel and Electric locomotive classes.
 * 
 * A locomotive is represented by two classes:
 *  LocomotiveSimulator - defines the behaviour, ie physics, motion, power generated etc
 *  LocomotiveViewer - defines the appearance in a 3D viewer including animation for wipers etc
 *  
 * Both these classes derive from corresponding classes for a basic TrainCar
 *  TrainCarSimulator - provides for movement, rolling friction, etc
 *  TrainCarViewer - provides basic animation for running gear, wipers, etc
 *  
 * Locomotives can either be controlled by a player, 
 * or controlled by the train's MU signals for brake and throttle etc.
 * The player controlled loco generates the MU signals which pass along to every
 * unit in the train.
 * For AI trains, the AI software directly generates the MU signals - there is no
 * player controlled train.
 * 
 * The end result of the physics calculations for the the locomotive is
 * a TractiveForce and a FrictionForce ( generated by the TrainCar class )
 * 
 */
/// COPYRIGHT 2009 by the Open Rails project.
/// This code is provided to enable you to contribute improvements to the open rails program.  
/// Use of the code for any other purpose or distribution of the code to anyone else
/// is prohibited without specific written permission from admin@openrails.org.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using MSTS;
using Microsoft.Xna.Framework.Input;
using Microsoft.Xna.Framework;



namespace ORTS
{


    ///////////////////////////////////////////////////
    ///   3D VIEW
    ///////////////////////////////////////////////////

    /// <summary>
    /// Adds animation for wipers to the basic TrainCar
    /// </summary>
    class LocomotiveViewer : TrainCarViewer
    {
        LocomotiveSimulator Locomotive;

        List<int> WiperPartIndexes = new List<int>();

        float WiperAnimationKey = 0;

        public LocomotiveViewer(Viewer viewer, LocomotiveSimulator car)
            : base(viewer, car)
        {
            Locomotive = car;

            // Find the animated parts
            if( TrainCarShape.SharedShape.Animations != null )
            {
                for (int iMatrix = 0; iMatrix < TrainCarShape.SharedShape.MatrixNames.Length; ++iMatrix)
                {
                    string matrixName = TrainCarShape.SharedShape.MatrixNames[iMatrix].ToUpper();
                    switch (matrixName)
                    {
                        case "WIPERARMLEFT1":
                        case "WIPERBLADELEFT1":
                        case "WIPERARMRIGHT1":
                        case "WIPERBLADERIGHT1":
                            if (TrainCarShape.SharedShape.Animations[0].FrameCount > 1)  // ensure shape file is properly animated for wipers
                                WiperPartIndexes.Add(iMatrix);
                            break;
                        case "MIRRORARMLEFT1":
                        case "MIRRORLEFT1":
                        case "MIRRORARMRIGHT1":
                        case "MIRRORRIGHT1":
                            // TODO
                            break;
                    }
                }
            }
        } 

        public override void Update(GameTime gameTime)
        {
            // Wiper animation
            if (WiperPartIndexes.Count > 0)  // skip this if there are no wipers
            {
                if (Locomotive.Wiper) // on
                {
                    // Wiper Animation
                    // Compute the animation key based on framerate etc
                    // ie, with 8 frames of animation, the key will advance from 0 to 8 at the specified speed.
                    WiperAnimationKey += ((float)TrainCarShape.SharedShape.Animations[0].FrameRate / 10f) * (float)gameTime.ElapsedGameTime.TotalMilliseconds / 1000.0f;
                    while (WiperAnimationKey >= TrainCarShape.SharedShape.Animations[0].FrameCount) WiperAnimationKey -= TrainCarShape.SharedShape.Animations[0].FrameCount;
                    while (WiperAnimationKey < -0.00001) WiperAnimationKey += TrainCarShape.SharedShape.Animations[0].FrameCount;
                    foreach (int iMatrix in WiperPartIndexes)
                        TrainCarShape.AnimateMatrix(iMatrix, WiperAnimationKey);
                }
                else // off
                {
                    if (WiperAnimationKey > 0.001)  // park the blades
                    {
                        WiperAnimationKey += ((float)TrainCarShape.SharedShape.Animations[0].FrameRate / 10f) * (float)gameTime.ElapsedGameTime.TotalMilliseconds / 1000.0f;
                        if (WiperAnimationKey >= TrainCarShape.SharedShape.Animations[0].FrameCount) WiperAnimationKey = 0;
                        foreach (int iMatrix in WiperPartIndexes)
                            TrainCarShape.AnimateMatrix(iMatrix, WiperAnimationKey);
                    }
                }
            }

            base.Update(gameTime);
        }

    } // Class LocomotiveViewer

}
