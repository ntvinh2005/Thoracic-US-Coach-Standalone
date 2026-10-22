using System;
using UnityEngine;

namespace SMMARTS
{

    public class UltrasoundVisibility : MonoBehaviour  // previously UltrasoundMaterial
    {

        [Header("-----UI text when touched in the US display-----", order = 0)]
        // This name is referenced when you touch/click on the object in the ultrasound display.
        // You can set this to simply the object (like "Lung") or
        // You have the freedom to say something more elaborate like "You touched blah blah blah" or "These are the whatevers"  
        // If you don't initialize this in the inspector, it just defaults to the name of the gameobject.
        public string TouchDisplayMessage = "";

        // This gives you the freedom to turn touch messages on and off on a per-object basis.
        // If you don't want to display a message when this specific object is touched (like during a test) set this to false.
        public bool EnableTouchDisplayMessage = true;

        [Header("-----Visible in Ultrasound?-----", order = 0)]
        // Default is to render the object on ultrasound.
        // there are times when this is not desireable, such as not rendering a scoring object that is there to simply accumulate hits when under the insonating beam.
        public bool ShowOnUltrasound = true;



        // Normal = simply render the Color on US screen. EdgeColor and EdgeThickness are not used.
        // Shadow = EdgeColor on surface with EdgeThickness, with shadow of Color. Shadow means you can't see anything past this object - perfect for bones.
        // Lung = EdgeColor on surface, with lung sliders and noise artifacts. Renders some sparkle-like texture below the edge.
        // Texture1 = Perlin-like noise using texture settings 1
        // Texture2 = Perlin-like noise using texture settings 2
        public enum RenderType { Normal, Shadow, Lung, Texture1, Texture2 };

        // These are presets for tissue types.
        // If you select one it will pre-load values for render type, color, edgecolor, and thickness.
        // as soon as you select one, it will snap back to "ApplyAPreset" and you will see some values change below. 
        // you can then tweak the values for color, edgecolor, etc as needed. 
        [Header("-----Ultrasound Appearance Presets-----", order = 0)]
        public UltrasoundPresets AppearancePreset = UltrasoundPresets.ApplyAPreset;

        [Header("-----Appearance in Ultrasound-----", order = 0)]
        public RenderType Type;

        // the color setting of this object
        public Color Color;

        // the edge color setting of this object, if applicable
        public Color EdgeColor;

        // the edge thickness setting of this object, if applicable
        [Range(0f, 0.25f)]
        public float EdgeThickness = 0f;

        public enum UltrasoundPresets
        {
            ApplyAPreset,
            Bone,
            Muscle,
            Ligament,
            Artery,
            Vein,
            Nerve
        }

        // Anisotropy Index.  
        // Has to do with how visible the object is in relation to the incident angle of the ultrasound insonating ray.
        // 0 (default) no anisotropy - the angle doesnt matter.
        // 90 (practically invisible) means that the object will be shown only for ultrasound insonating rays that are 90 degrees to the object's surface.
        // 45 (invisible past 45 degrees) the object will begin to fade out at about 70 degrees, and disappear at about 45 degrees. pretty extreme.
        // 65 (invisible past 65 degrees) seems about accurate for needles and lungs on newer US machines. 
        [Range(0, 90)]
        public float AnisoIndex = 0;

        // IN PROGRESS 
        // [Header("Rendering Layer Order")]
        // [Tooltip("Rendering layer - lower numbers render behind higher numbers (0 = background, 1 = default)")]
        // public int LayerOrder = 1;

        // The following methods provide the means to test if this object is visible in US.

        // This is an accumulator for ultrasound insonating beam hits.
        // if the material is not under the insonating beam, this accumulator stays zero.
        // You can reference this value to determine if the user is viewing the object.
        // It should be polled on Update, and is cleared in LateUpdate.
        private int hitCount;


        // on Start(), if TouchDisplayMessage isn't assigned, just load it with the Gameobject's name.
        // That way, if you click on this object in the US display, you at least get something.
        void Start()
        {
            if (TouchDisplayMessage == "") TouchDisplayMessage = transform.name;
        }

        public int GetHitCount()
        {
            return hitCount;
        }

        public void NoteHit()
        {
            hitCount++;
        }

        void LateUpdate()
        {
            hitCount = 0;
        }

        // this editor-only function gets called whenever the user changes something in the inspector.
        // its good for loading presets, etc. 
        public void OnValidate()
        {
            // handle ultrasound presets
            // once these are set, you have the freedom to tweak each instance in the inspector.
            if (AppearancePreset != UltrasoundPresets.ApplyAPreset)
            {
                if (AppearancePreset == UltrasoundPresets.Bone)
                {
                    Type = RenderType.Shadow;
                    Color = Color.black;
                    EdgeColor = Color.white;
                    EdgeThickness = 0.02f;
                    AnisoIndex = 0;
                }
                if (AppearancePreset == UltrasoundPresets.Muscle)
                {
                    Type = RenderType.Texture1;
                    AnisoIndex = 0;
                }
                if (AppearancePreset == UltrasoundPresets.Ligament)
                {
                    Type = RenderType.Normal;
                    Color = new Color(29, 29, 29);
                    AnisoIndex = 0;
                }
                if (AppearancePreset == UltrasoundPresets.Artery)
                {
                    Type = RenderType.Normal;
                    Color = Color.black;
                    AnisoIndex = 0;
                }
                if (AppearancePreset == UltrasoundPresets.Nerve)
                {
                    Type = RenderType.Normal;
                    Color = Color.black;
                    AnisoIndex = 0;
                }
                if (AppearancePreset == UltrasoundPresets.Nerve)
                {
                    Type = RenderType.Normal;
                    Color = new Color(84, 84, 84);
                    AnisoIndex = 20;
                }
                AppearancePreset = UltrasoundPresets.ApplyAPreset; // Reset to ready state
            }
        }

    }
}