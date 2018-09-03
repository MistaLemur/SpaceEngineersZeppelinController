using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ZepController
{
    //This is a simple PID controller class.
    public class PID
    {
        //default coefficients make a unity gain controller

        public double kP = 1;
        public double kI = 0;
        public double kD = 0;

        double errorSum = 0;
        double lastError = 0;

        public double integralDecay = 0.75; //Since whip does it too right
                                            //I actually haven't seen this in industry.

        long steps = 0;

        public PID()
        {

        }

        public PID(double P, double I, double D)
        {
            kP = P;
            kI = I;
            kD = D;
        }

        public double ControllerResponse(double error, double timeStep)
        {
            //this computes the controller response to the given error and timestep

            if (kI != 0)
            {
                //trapezoidal rule for integration. This could be overkill
                if (integralDecay != 0)
                {
                    errorSum *= integralDecay;
                }
                errorSum += (error + lastError) * 0.5 * timeStep;
                //If timeStep is constant, it can be taken out of this equation to reduce the number of floating point operations per tick
            }

            double errorD = (error - lastError) / timeStep;
            //If timeStep is constant, it can be taken out of this equation to reduce the number of floating point operations per tick
            if (steps++ == 0) errorD = 0;


            double y = kP * error;
            if (kD != 0) y += kD * errorD;
            if (kI != 0) y += kI * errorSum;

            lastError = error;

            return y;
        }

        public void Reset()
        {
            //this resets the state of the controller
            steps = 0;
            errorSum = 0;
            lastError = 0;
        }
    }
}
