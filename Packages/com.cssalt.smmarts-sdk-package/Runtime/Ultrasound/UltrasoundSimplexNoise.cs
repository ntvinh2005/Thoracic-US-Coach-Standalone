using UnityEngine;

namespace SMMARTS
{
	public static class UltrasoundSimplexNoise
	{

        [Header("-----SMMARTS SDK 2025-----", order = 0)]

        //A "random" array of integers used to create the pseudorandom simplex
        private static int[] hash = {
		151,160,137, 91, 90, 15,131, 13,201, 95, 96, 53,194,233,  7,225,
		140, 36,103, 30, 69,142,  8, 99, 37,240, 21, 10, 23,190,  6,148,
		247,120,234, 75,  0, 26,197, 62, 94,252,219,203,117, 35, 11, 32,
		 57,177, 33, 88,237,149, 56, 87,174, 20,125,136,171,168, 68,175,
		 74,165, 71,134,139, 48, 27,166, 77,146,158,231, 83,111,229,122,
		 60,211,133,230,220,105, 92, 41, 55, 46,245, 40,244,102,143, 54,
		 65, 25, 63,161,  1,216, 80, 73,209, 76,132,187,208, 89, 18,169,
		200,196,135,130,116,188,159, 86,164,100,109,198,173,186,  3, 64,
		 52,217,226,250,124,123,  5,202, 38,147,118,126,255, 82, 85,212,
		207,206, 59,227, 47, 16, 58, 17,182,189, 28, 42,223,183,170,213,
		119,248,152,  2, 44,154,163, 70,221,153,101,155,167, 43,172,  9,
		129, 22, 39,253, 19, 98,108,110, 79,113,224,232,178,185,112,104,
		218,246, 97,228,251, 34,242,193,238,210,144, 12,191,179,162,241,
		 81, 51,145,235,249, 14,239,107, 49,192,214, 31,181,199,106,157,
		184, 84,204,176,115,121, 50, 45,127,  4,150,254,138,236,205, 93,
		222,114, 67, 29, 24, 72,243,141,128,195, 78, 66,215, 61,156,180,

		151,160,137, 91, 90, 15,131, 13,201, 95, 96, 53,194,233,  7,225,
		140, 36,103, 30, 69,142,  8, 99, 37,240, 21, 10, 23,190,  6,148,
		247,120,234, 75,  0, 26,197, 62, 94,252,219,203,117, 35, 11, 32,
		 57,177, 33, 88,237,149, 56, 87,174, 20,125,136,171,168, 68,175,
		 74,165, 71,134,139, 48, 27,166, 77,146,158,231, 83,111,229,122,
		 60,211,133,230,220,105, 92, 41, 55, 46,245, 40,244,102,143, 54,
		 65, 25, 63,161,  1,216, 80, 73,209, 76,132,187,208, 89, 18,169,
		200,196,135,130,116,188,159, 86,164,100,109,198,173,186,  3, 64,
		 52,217,226,250,124,123,  5,202, 38,147,118,126,255, 82, 85,212,
		207,206, 59,227, 47, 16, 58, 17,182,189, 28, 42,223,183,170,213,
		119,248,152,  2, 44,154,163, 70,221,153,101,155,167, 43,172,  9,
		129, 22, 39,253, 19, 98,108,110, 79,113,224,232,178,185,112,104,
		218,246, 97,228,251, 34,242,193,238,210,144, 12,191,179,162,241,
		 81, 51,145,235,249, 14,239,107, 49,192,214, 31,181,199,106,157,
		184, 84,204,176,115,121, 50, 45,127,  4,150,254,138,236,205, 93,
		222,114, 67, 29, 24, 72,243,141,128,195, 78, 66,215, 61,156,180
	};

		//The number of ints in hash
		private const int hashMask = 255;

		//A "random" array of Vector3s used to create the pseudorandom effects of the simplex
		private static Vector3[] simplexGradients3D = {
		new Vector3( 1f, 1f, 0f).normalized,
		new Vector3(-1f, 1f, 0f).normalized,
		new Vector3( 1f,-1f, 0f).normalized,
		new Vector3(-1f,-1f, 0f).normalized,
		new Vector3( 1f, 0f, 1f).normalized,
		new Vector3(-1f, 0f, 1f).normalized,
		new Vector3( 1f, 0f,-1f).normalized,
		new Vector3(-1f, 0f,-1f).normalized,
		new Vector3( 0f, 1f, 1f).normalized,
		new Vector3( 0f,-1f, 1f).normalized,
		new Vector3( 0f, 1f,-1f).normalized,
		new Vector3( 0f,-1f,-1f).normalized,

		new Vector3( 1f, 1f, 0f).normalized,
		new Vector3(-1f, 1f, 0f).normalized,
		new Vector3( 1f,-1f, 0f).normalized,
		new Vector3(-1f,-1f, 0f).normalized,
		new Vector3( 1f, 0f, 1f).normalized,
		new Vector3(-1f, 0f, 1f).normalized,
		new Vector3( 1f, 0f,-1f).normalized,
		new Vector3(-1f, 0f,-1f).normalized,
		new Vector3( 0f, 1f, 1f).normalized,
		new Vector3( 0f,-1f, 1f).normalized,
		new Vector3( 0f, 1f,-1f).normalized,
		new Vector3( 0f,-1f,-1f).normalized,

		new Vector3( 1f, 1f, 1f).normalized,
		new Vector3(-1f, 1f, 1f).normalized,
		new Vector3( 1f,-1f, 1f).normalized,
		new Vector3(-1f,-1f, 1f).normalized,
		new Vector3( 1f, 1f,-1f).normalized,
		new Vector3(-1f, 1f,-1f).normalized,
		new Vector3( 1f,-1f,-1f).normalized,
		new Vector3(-1f,-1f,-1f).normalized
	};

		//The number of Vector3s in SimplexGradients3D
		private const int simplexGradientsMask3D = 31;

		//Simple method, computes the dot product between a Vector3 and the x y and z components of a vector.
		private static float Dot(Vector3 g, float x, float y, float z)
		{
			return g.x * x + g.y * y + g.z * z;
		}

		//Multiplier used by the program
		private static float simplexScale3D = 8192f * Mathf.Sqrt(3f) / 375f;


		//This helper method is only ever called by the Simplex3D method and is the only method where SimplexGradient 
		//values are actually retrieved.
		private static Vector4 Simplex3DPart(Vector3 point, int ix, int iy, int iz)
		{
			float unskew = (ix + iy + iz) * (1f / 6f);
			float x = point.x - ix + unskew;
			float y = point.y - iy + unskew;
			float z = point.z - iz + unskew;
			float f = 0.5f - x * x - y * y - z * z;
			Vector4 sample = new Vector4();
			if (f > 0f)
			{
				float f2 = f * f;
				float f3 = f * f2;
				Vector3 g = simplexGradients3D[hash[hash[hash[ix & hashMask] + iy & hashMask] + iz & hashMask] & simplexGradientsMask3D];
				float v = Dot(g, x, y, z);
				float v6f2 = -6f * v * f2;
				sample.w = v * f3;
				sample.x = g.x * f3 + v6f2 * x;
				sample.y = g.y * f3 + v6f2 * y;
				sample.z = g.z * f3 + v6f2 * z;
			}
			return sample;
		}

		//With the help of the Simplex3DPart method, this method uses the vector "point" in conjunction
		//with the Hash and SimplexGradient arrays to find a simplex point in space that corresponds to
		//The given input point. Because these values in the arrays are not randomly created every time
		//This class is used, the same input will always yield the same output.

		//Divisor is the most important input for our purposes. It ensures that the change in z direction
		//Only affects the change in the noise by a fraction of what it would normally do. A divisor of 4
		//Will create noise that for every 4 units moved in the z direction only moves the noise by 1 unit

		public static float Simplex3D(Vector3 point)
		{
			float skew = (point.x + point.y + point.z) * (1f / 3f);
			float sx = point.x + skew;
			float sy = point.y + skew;
			float sz = point.z + skew;
			int ix = Mathf.FloorToInt(sx);
			int iy = Mathf.FloorToInt(sy);
			int iz = Mathf.FloorToInt(sz);
			Vector4 sample = Simplex3DPart(point, ix, iy, iz);
			sample += Simplex3DPart(point, ix + 1, iy + 1, iz + 1);
			float x = sx - ix;
			float y = sy - iy;
			float z = sz - iz;
			if (x >= y)
			{
				if (x >= z)
				{
					sample += Simplex3DPart(point, ix + 1, iy, iz);
					if (y >= z)
					{
						sample += Simplex3DPart(point, ix + 1, iy + 1, iz);
					}
					else
					{
						sample += Simplex3DPart(point, ix + 1, iy, iz + 1);
					}
				}
				else
				{
					sample += Simplex3DPart(point, ix, iy, iz + 1);
					sample += Simplex3DPart(point, ix + 1, iy, iz + 1);
				}
			}
			else
			{
				if (y >= z)
				{
					sample += Simplex3DPart(point, ix, iy + 1, iz);
					if (x >= z)
					{
						sample += Simplex3DPart(point, ix + 1, iy + 1, iz);
					}
					else
					{
						sample += Simplex3DPart(point, ix, iy + 1, iz + 1);
					}
				}
				else
				{
					sample += Simplex3DPart(point, ix, iy, iz + 1);
					sample += Simplex3DPart(point, ix, iy + 1, iz + 1);
				}
			}
			return sample.w * simplexScale3D;
		}
	}
}
