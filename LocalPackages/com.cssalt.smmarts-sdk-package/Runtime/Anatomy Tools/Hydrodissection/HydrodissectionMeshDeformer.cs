using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace SMMARTS
{

    public class HydrodissectionMeshDeformer : MonoBehaviour
    {

        /*
        Overview
        •	HydrodissectionMeshDeformer is a Monobehavior script that the programmer pre-places on each hydrodissectable mesh’ gameobject in the hierarchy, for example on a muscle
        •	Responsible for the movement of all the vertices in a hydrodissectable mesh
        •	Moves vertices from an initial position to a point in 3D space determined by the distance of the vertex’ initial position to the needle tip, InjectateVolume_ml, and the TissueHydrodissectionConstant
        •	Won’t move a vertex farther then VertexDisplacementLimit_mm from its initial position
        •	Won’t move a vertex beyond the boundaries of the SphereOfInfluence
        •	Updates the mesh collider with the new vertex positions, so the mesh can be seen in simulated ultrasound as well as 3D
        •	Can reset all its vertices back to their initial positions, but the initial positions need to be read after the tracking system connects.
        */


        // Variables

        public Mesh MyMesh;
        //	A reference to the mesh component of the gameobject, which is what is rendered on the screen in 3D space; this is what the cameras see. 
        //	This instance of HydrodissectionMeshDeformer will periodically set MyMesh to a Vector3[] array.

        public MeshCollider MyMeshCollider;
        //  A reference to the mesh collider of the gameobject. its different than what you see on the screen; this is for the physics model.
        //  since our ultrasound uses the physics model, and since we want what we see in ultrasound to match what we see in 3D space,
        //  we have to periodically set MyMeshCollider to the same Vector3[] array as well.

        public Vector3[] Vertices;
        //  A working copy of vertices of this mesh.
        //  In Unity, you may change a mesh's vertices only by setting its mesh vertex list equal to a Vector3[] array.
        //  You cannot edit a single mesh vertex. Although the code will compile, you won't see your vertex move.
        //  So, we will make this Vertices[] copy and change it, one element at a time, instead of the mesh directly.
        //  Then we set the mesh vertices to our Vertices[] copy all at once, in LateUpdate().

        Vector3[] InitialVertexPositions;
        //	The initial positions of all the vertices in space at the beginning of each hydrodissection, in three degrees of freedom (no rotation)

        Vector3[] OriginalVertexPositions;
        //  the initial positions of all the vertices at startup

        float distanceFromInitialPositionToNeedleTip_mm;
        //	The distance from a vertex’ initial position to the NeedleTip’s position

        float displacement_mm;
        //	the distance in mm that a vertex will travel. The distance is determined by how far the needle tip is from a vertex’ InitialPosition, InjectateVolume_ml, and the TissueHydrodissectionConstant

        public bool MeshMoved;
        //	true or false flag that means at least one of the vertices in MyMesh moved. just because there is Injectate there might not be mesh movement, as would be the case if the needle tip was too far from every vertex in MyMesh.
        //	read by this script. if true, this script will update the mesh collider of the mesh
        //	reset to false at the end of each frame

        bool InjectingLastFrame;
        // used to test for beginning of hydrodissection

        Vector3 DisplacementDirection;
        // The normalized direction of displacement for all vertices during an injection.
        // Recalculated at the beginning of each injection.
        // Equal to the direction from the needle tip to the vertex closest to the needle tip when injection began.
        // When injection begins, the vertex closest to the needle tip is located.
        // The direction of displacement is along a line virtually drawn between the needle tip and this vertex.
        // This direction is used for all vertices in the muscle mesh until injection stops and is recalculated when injection begins again.
        // Changing the direction more frequencly was tested and it was not necessary.


        // this is an internal flag for initialization. 
        // if the tracking system is running, as soon as it connects it moves the anatomy to align with the index plate.
        // since this mesh is part of the anatomy, it moves too.
        // Our mesh vertices need to be in their correct positions before we begin reading their initial vertex positions.
        // the Start, Update, and LateUpdate return early if this flag is false.
        bool Ready = false;

        // Functions

        //	Start () runs automatically before the first frame
        //	Sets MyMesh to the mesh component of the gameobject on which this script has been attached 
        //	Sets the InitialVertexPositions of all the vertices in MyMesh - which means waiting for the tracking system (ATC) to connect, if present.
        //  If the tracking system is present but not connected yet, it invokes itself in one second and closes early.
        private void Start()
        {

            // we'll proceed with the start after the tracking system is finished moving the anatomy to the index plate.
            // we are moving mesh vertices in world space coordinates so we let the system settle first.
            if (ATC.ME != null) // if null, no tracking system found, probably for development, so just proceed.
            {
                if (ATC.ME.Connected == false) // the tracking system is on, but it has not connected yet.
                {
                    Invoke("Start", 1); // try again in one second. The tracking system takes a few seconds to wake up.
                    return;
                }
            }
            

            // grab the initial vertex positions. They are in a Vector3 array. You have to clone the array to have a separate copy.
            MyMesh = GetComponent<MeshFilter>().mesh;
            Vertices = MyMesh.vertices;
            OriginalVertexPositions = Vertices.Clone() as Vector3[];

            // mesh vertices are in relation to the coordinate system of the mesh during import. We want them in world space.
            // A vertex in MyMesh.vertices was set by outside software, like CAD or a sculpting tool; its the vertex position in the .fbx file.
            // we want to reference the vertex positions we see, in world space - after we have placed them in anatomy and in whatever transform.
            for (int i = 0; i < OriginalVertexPositions.Length; i++)
                OriginalVertexPositions[i] = transform.TransformPoint(OriginalVertexPositions[i]);
            // note that after you manipulate a vertex in this array, in world space,
            // you will have to transform it back into mesh space using transform.InverseTransformPoint() before you upload it to the mesh

            // you will also have to work with vertices in world space, so clone what you just did to convert mesh to world coordinates.
            Vertices = OriginalVertexPositions.Clone() as Vector3[];  // the working copy
            InitialVertexPositions = OriginalVertexPositions.Clone() as Vector3[]; // initial positions at the start of each injection

            // Debug.Log("Meshes Initialized!");

            // Let other scripts know its safe to run, we have initialized safely
            Ready = true;
        }

        public void InitializeNewHydrodissection()
        {

            // find closest point on mesh to needle tip and use that as a direction vector for all vertex movement.
            Vector3 closestVertexFoundSoFar = Vector3.positiveInfinity; // starting with a huge number
            float minimumDistanceFoundSoFar = Mathf.Infinity;           // starting with a huge number
            for (int i = 0; i < Vertices.Length; i++)
            {
                float distance_from_tip_to_vertex = Vector3.Distance(HydrodissectionManager.ME.HydrodissectionPoint, Vertices[i]);
                if (distance_from_tip_to_vertex < minimumDistanceFoundSoFar)
                {
                    minimumDistanceFoundSoFar = distance_from_tip_to_vertex;
                    closestVertexFoundSoFar = Vertices[i];
                }

                // while we are searching all the vertices, we can also load the initial vertex positions for the start of this injection.
                InitialVertexPositions[i] = Vertices[i];

            }

            // set the direction to move! 
            DisplacementDirection = (closestVertexFoundSoFar - HydrodissectionManager.ME.HydrodissectionPoint).normalized;

        }

        //	Update() runs every frame, just before each frame is rendered to the screen
        //	Checks if HydrodissectionManager’s Injecting flag is true, and if so, loops through each vertex in MyMesh:
        //	measures distanceFromInitialPositionToNeedleTip_mm for the vertex
        //	Tests if the vertex is within HydrodissectionManager’s SphereOfInfluence centered on the NeedleTipPosition. If the distanceFromInitialPositionToNeedleTip is less than the SphereOfInfluenceRadius_mm, 
        //	Calculates the vertex displacement_mm = InjectateVolume * TissueHydrodissectionConstant / distanceFromInitialPositionToNeedleTip
        //	Limits vertex displacement_mm to VertexDisplacementLimit_mm (prevents the vertex from moving too far when the needle tip is very close)
        //	moves the vertex from its initial position by displacement_mm in the direction facing away from NeedleTipPosition (the vertex moves radially away from the needle tip)
        //	sets the MeshMoved flag to true
        void Update()
        {
            // if were not ready, that means the tracking system is still starting up and the meshes are not yet in place. do nothing.
            if (!Ready) return;

            // detect the start of a new hydrodissection from looking at when the conditions go from false to true
            if (HydrodissectionManager.ME.Injecting & !InjectingLastFrame) InitializeNewHydrodissection();
            InjectingLastFrame = HydrodissectionManager.ME.Injecting;

            if (HydrodissectionManager.ME.Injecting == false) return;

            // move the vertices if they are within the sphere of influence
            for (int i = 0; i < Vertices.Length; i++)
            {
                distanceFromInitialPositionToNeedleTip_mm = Vector3.Distance(HydrodissectionManager.ME.HydrodissectionPoint, OriginalVertexPositions[i]); // InitialVertexPositions[i]);
                if (distanceFromInitialPositionToNeedleTip_mm < HydrodissectionManager.ME.SphereOfInfluenceRadius_mm)
                {
                    displacement_mm = HydrodissectionManager.ME.InjectateVolume_ml *
                        HydrodissectionManager.ME.TissueHydrodissectionConstant / distanceFromInitialPositionToNeedleTip_mm;

                    // limit the vertex displacement so its not moved too far. dont just test the displacement_mm, test the displacement_mm + however much it moved from the last bolus.
                    float previousDisplacement_mm = Vector3.Distance(Vertices[i], OriginalVertexPositions[i]);
                    if (displacement_mm > HydrodissectionManager.ME.VertexDisplacementLimit_mm) displacement_mm = HydrodissectionManager.ME.VertexDisplacementLimit_mm;

                    // move the vertex value
                    Vertices[i] = InitialVertexPositions[i] + (DisplacementDirection * displacement_mm);

                    // this flag means that the vertices we moved will be loaded into the mesh renderer and mesh collider
                    MeshMoved = true;
                }
            }

        }

        //  LateUpdate() runs every frame, after Update(), and after a frame has rendered
        //	If the MeshMoved flag is true, updates the mesh and the collider on this gameobject so the simulated ultrasound, which can only see mesh colliders, reflects the changes in the mesh from hydrodissection 
        //	Resets the MeshMoved flag to false
        void LateUpdate()
        {
            // if were not ready, that means the tracking system is still starting up and the meshes are not yet in place. do nothing.
            if (!Ready) return;

            if (MeshMoved)
            {
                // UPLOAD NEW MESH for 3D rendering
                // The entire set of vertices must be uploaded all at once, as a Vector3[]. We can't edit a single vertex at a time.

                // the mesh drawn on the screen will be in mesh file coordinate space, and we have been working with world coordinates.
                // we must convert from world back to mesh space using InverseTransformPoint().

                Vector3[] mesh_coordinates = Vertices.Clone() as Vector3[];
                for (int i = 0; i < mesh_coordinates.Length; i++)
                    mesh_coordinates[i] = transform.InverseTransformPoint(mesh_coordinates[i]);


                MyMesh.vertices = mesh_coordinates; // we upload the vertices;
                MyMesh.RecalculateNormals(); // smooths out the hard edges

                // UPLOAD A NEW COLLIDER for the physics.raycasts used in ultrasound rendering
                // performance-wise, colliders are more expensive than the mesh renderer becuase physics engine stuff.
                // if (FrameConductor.DeformableAnatomyFrame) // you could do this every other frame, and render US on the other frame.
                MyMeshCollider.sharedMesh = MyMesh;

                // reset the MeshMoved flag we use to signal that the mesh must be changed to match the new vertices.
                MeshMoved = false;
            }
        }

        //	ResetMesh()
        //	Loops through each vertex in MyMesh and sets the vertex positions to their InitialPosition
        //	Sets the MeshMoved flag to true
        public void ResetMesh()
        {
            // if were not ready, that means the tracking system is still starting up and the meshes are not yet in place. do nothing.
            if (!Ready) return;

            // Debug.Log("Hydro Mesh Deformer reset called on " + gameObject );
            Vertices = OriginalVertexPositions.Clone() as Vector3[];

            MeshMoved = true;

        }

    }
}
