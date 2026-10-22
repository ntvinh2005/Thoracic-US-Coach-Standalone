using UnityEngine;
using System.Collections;
using System;
using UnityEngine.UI;
using System.Timers;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace SMMARTS
{
    public class Toast
    {

        [DllImport("WindowsVoice")]
        public static extern void initSpeech();
        [DllImport("WindowsVoice")]
        public static extern void destroySpeech();
        [DllImport("WindowsVoice")]
        public static extern void addToSpeechQueue(string s);


        static readonly bool hardDisableTextToSpeechOutput = false;

        public enum Type
        {
            ERROR,
            WARNING,
            MESSAGE,
            GREEN,
            YELLOW,
            BLUE,
            MAGENTA,
            CYAN,
            CLEAR,
            BLACK,
            WHITE,
            GREY,
            RED
        }

        public enum Gravity
        {
            BOTTOM,
            CENTER,
            TOP,
            TOP_RIGHT,
            BOTTOM_RIGHT
        }

        public enum Sound
        {
            NONE,
            BEEP,
            CHIME,
            WARNING,
            TEXTTOSPEECH
        }

        struct ToastHolder
        {
            public MonoBehaviour toastContext;
            public string toastMessage;
            public int toastDuration;
            public Type toastType;
            public Gravity toastGravity;
            public int toastSize;
            public Sound toastSound;
            public string textToSpeech;

            public ToastHolder(MonoBehaviour mContext, string mMessage, int mDuration, Type mType, int mSize, Gravity mGravity, Sound mSound, string mTTS)
            {
                toastContext = mContext;
                toastMessage = mMessage;
                toastDuration = mDuration;
                toastType = mType;
                toastSize = mSize;
                toastGravity = mGravity;
                toastSound = mSound;
                textToSpeech = mTTS;
            }
        }

        public static bool isActive { get; private set; }

        static GameObject toastCanvas;
        static MonoBehaviour ctxt;
        static int durationSecs;
        private static Queue<ToastHolder> toastQueue = new Queue<ToastHolder>();
        private static Coroutine currentTimer;
        private static readonly int DEFAULT_SIZE = 20;

        static Toast()
        {
           //  initSpeech();
        }

        // -------------- NO SOUND TOASTS ------------------------ //
        public static void Show(MonoBehaviour caller, string message, int duration, Type type)
        {
            if (isActive)
            {

                //Debug.Log("ENQUEUED ONE SIMPLE");
                toastQueue.Enqueue(new ToastHolder(caller, message, duration, type, DEFAULT_SIZE, Gravity.BOTTOM, Sound.NONE, null));     //Default size and gravity.
                return;
            }

            var prefab = Resources.Load("ScalingToast");
            if (prefab == null)
            {
                Debug.LogAssertion("Prefab missing!");
                return;
            }


            //Debug.Log("CREATED ONE");

            //Instantiate the toast!
            toastCanvas = (GameObject)UnityEngine.Object.Instantiate(prefab);

            //Get the text within the toast and set it.
            toastCanvas.GetComponentInChildren<Text>().text = message;
            toastCanvas.GetComponentInChildren<Text>().fontSize = DEFAULT_SIZE;       //Default font size is 20!
                                                                                      //Color.
                                                                                      //Get the toast type and set the mood accordingly.
            switch (type)
            {
                case Type.ERROR:
                    toastCanvas.GetComponentInChildren<Image>().color = new Color(1f, 0.011f, 0.011f);
                    break;
                case Type.WARNING:
                    toastCanvas.GetComponentInChildren<Image>().color = new Color(1f, 0.53125f, 0.03125f);
                    break;
                case Type.MESSAGE:
                    toastCanvas.GetComponentInChildren<Image>().color = new Color(0f, 0f, 0f);
                    break;
                case Type.BLACK:
                    toastCanvas.GetComponentInChildren<Image>().color = Color.black;
                    break;
                case Type.BLUE:
                    toastCanvas.GetComponentInChildren<Image>().color = Color.blue;
                    break;
                case Type.CLEAR:
                    toastCanvas.GetComponentInChildren<Image>().color = Color.clear;
                    break;
                case Type.CYAN:
                    toastCanvas.GetComponentInChildren<Image>().color = Color.cyan;
                    break;
                case Type.GREY:
                    toastCanvas.GetComponentInChildren<Image>().color = Color.grey;
                    break;
                case Type.GREEN:
                    toastCanvas.GetComponentInChildren<Image>().color = Color.green;
                    break;
                case Type.MAGENTA:
                    toastCanvas.GetComponentInChildren<Image>().color = Color.magenta;
                    break;
                case Type.WHITE:
                    toastCanvas.GetComponentInChildren<Image>().color = Color.white;
                    break;
                case Type.RED:
                    toastCanvas.GetComponentInChildren<Image>().color = Color.red;
                    break;
            }

            isActive = true;
            durationSecs = duration;
            ctxt = caller;
            currentTimer = ctxt.StartCoroutine(DestroyToast());


            //Play the animation
            string animString = getGravityString(Gravity.BOTTOM);
            AnimationClip anim = Resources.Load<AnimationClip>(animString);
            anim.legacy = true;
            toastCanvas.GetComponentInChildren<Animation>().Stop();
            toastCanvas.GetComponent<Animation>().AddClip(anim, animString);
            toastCanvas.GetComponentInChildren<Animation>().clip = anim;
            toastCanvas.GetComponentInChildren<Animation>().Play();


            //HERE
            //
            toastCanvas.GetComponentInChildren<AudioSource>().enabled = true;
            //
            //AUDIO
        }

        public static void Show(MonoBehaviour caller, string message, int duration, Type type, int size)
        {
            if (isActive)
            {
                //Debug.Log("ENQUEUED ONE BIG");
                toastQueue.Enqueue(new ToastHolder(caller, message, duration, type, size, Gravity.BOTTOM, Sound.NONE, null));
                return;
            }
            else
            {

                //Show the regular toast and then increase the size.
                Show(caller, message, duration, type);

                toastCanvas.GetComponentInChildren<Text>().fontSize = size;
                //Debug.Log("SETTING THE SIZE to " + size);

            }
        }

        public static void Show(MonoBehaviour caller, string message, int duration, Type type, Gravity gravity)
        {
            if (isActive)
            {
                //Debug.Log("ENQUEUED ONE, GRAVITY DEFAULT");
                toastQueue.Enqueue(new ToastHolder(caller, message, duration, type, DEFAULT_SIZE, gravity, Sound.NONE, null));
                return;
            }
            else
            {

                //Show the regular toast and then increase the size.
                Show(caller, message, duration, type);

                //toastCanvas.GetComponentInChildren<Text>().fontSize = DEFAULT_SIZE;
                //Debug.Log("SETTING THE SIZE to " + DEFAULT_SIZE);

                string animString = getGravityString(gravity);
                AnimationClip anim = Resources.Load<AnimationClip>(animString);
                anim.legacy = true;
                toastCanvas.GetComponentInChildren<Animation>().Stop();
                toastCanvas.GetComponent<Animation>().AddClip(anim, animString);
                toastCanvas.GetComponentInChildren<Animation>().clip = anim;
                toastCanvas.GetComponentInChildren<Animation>().Play();
            }
        }

        public static void Show(MonoBehaviour caller, string message, int duration, Type type, int size, Gravity gravity)
        {
            if (isActive)
            {
                //Debug.Log("ENQUEUED ONE GRAVITY DEFAULT");
                toastQueue.Enqueue(new ToastHolder(caller, message, duration, type, size, gravity, Sound.NONE, null));
                return;
            }
            else
            {

                //Show the regular toast and then increase the size.
                Show(caller, message, duration, type);

                toastCanvas.GetComponentInChildren<Text>().fontSize = size;
                //Debug.Log("SETTING THE SIZE to " + DEFAULT_SIZE);

                string animString = getGravityString(gravity);
                AnimationClip anim = Resources.Load<AnimationClip>(animString);
                anim.legacy = true;
                toastCanvas.GetComponentInChildren<Animation>().Stop();
                toastCanvas.GetComponent<Animation>().AddClip(anim, animString);
                toastCanvas.GetComponentInChildren<Animation>().clip = anim;
                toastCanvas.GetComponentInChildren<Animation>().Play();
            }
        }

        // -------------- SIMPLE SOUND TOASTS ------------------------ //
        public static void Show(MonoBehaviour caller, string message, int duration, Type type, Sound sound)
        {
            if (isActive)
            {
                //Debug.Log("ENQUEUED ONE BIG");
                toastQueue.Enqueue(new ToastHolder(caller, message, duration, type, DEFAULT_SIZE, Gravity.BOTTOM, sound, null));
                return;
            }
            else
            {

                //Show the regular toast and then increase the size.
                Show(caller, message, duration, type);


                AudioSource AS = toastCanvas.GetComponentInChildren<AudioSource>();
                switch (sound)
                {
                    case Sound.NONE:
                        break;
                    case Sound.BEEP:
                        AS.clip = Resources.Load<AudioClip>("AlertAudio2");
                        AS.Play();
                        break;
                    case Sound.CHIME:
                        AS.clip = Resources.Load<AudioClip>("DingAudio2");
                        AS.Play();
                        break;
                    case Sound.WARNING:
                        AS.clip = Resources.Load<AudioClip>("AlertAudio2");
                        AS.Play();
                        break;
                }
            }
        }

        public static void Show(MonoBehaviour caller, string message, int duration, Type type, int size, Sound sound)
        {

            if (isActive)
            {
                //Debug.Log("ENQUEUED ONE BIG");
                toastQueue.Enqueue(new ToastHolder(caller, message, duration, type, size, Gravity.BOTTOM, sound, null));
                return;
            }
            else
            {

                toastCanvas.GetComponentInChildren<Text>().fontSize = size;
                //Show the regular toast and then increase the size.
                Show(caller, message, duration, type);

                AudioSource AS = toastCanvas.GetComponentInChildren<AudioSource>();
                switch (sound)
                {
                    case Sound.NONE:
                        break;
                    case Sound.BEEP:
                        AS.clip = Resources.Load<AudioClip>("AlertAudio2");
                        AS.Play();
                        break;
                    case Sound.CHIME:
                        AS.clip = Resources.Load<AudioClip>("DingAudio2");
                        AS.Play();
                        break;
                    case Sound.WARNING:
                        AS.clip = Resources.Load<AudioClip>("WarningAudio2");
                        AS.Play();
                        break;
                }
            }
        }

        public static void Show(MonoBehaviour caller, string message, int duration, Type type, Gravity gravity, Sound sound)
        {
            if (isActive)
            {
                //Debug.Log("ENQUEUED ONE BIG");
                toastQueue.Enqueue(new ToastHolder(caller, message, duration, type, DEFAULT_SIZE, gravity, sound, null));
                return;
            }
            else
            {

                //Show the regular toast and then increase the size.
                Show(caller, message, duration, type);



                string animString = getGravityString(gravity);
                AnimationClip anim = Resources.Load<AnimationClip>(animString);
                anim.legacy = true;
                toastCanvas.GetComponentInChildren<Animation>().Stop();
                toastCanvas.GetComponent<Animation>().AddClip(anim, animString);
                toastCanvas.GetComponentInChildren<Animation>().clip = anim;
                toastCanvas.GetComponentInChildren<Animation>().Play();


                AudioSource AS = toastCanvas.GetComponentInChildren<AudioSource>();
                switch (sound)
                {
                    case Sound.NONE:
                        break;
                    case Sound.BEEP:
                        AS.clip = Resources.Load<AudioClip>("AlertAudio2");
                        AS.Play();
                        break;
                    case Sound.CHIME:
                        AS.clip = Resources.Load<AudioClip>("DingAudio2");
                        AS.Play();
                        break;
                    case Sound.WARNING:
                        AS.clip = Resources.Load<AudioClip>("WarningAudio2");
                        AS.Play();
                        break;
                }
            }
        }

        public static void Show(MonoBehaviour caller, string message, int duration, Type type, int size, Gravity gravity, Sound sound)
        {
            if (isActive)
            {
                //Debug.Log("ENQUEUED ONE BIG");
                toastQueue.Enqueue(new ToastHolder(caller, message, duration, type, size, gravity, sound, null));
                return;
            }
            else
            {

                //Show the regular toast and then increase the size.
                Show(caller, message, duration, type);

                toastCanvas.GetComponentInChildren<Text>().fontSize = size;

                string animString = getGravityString(gravity);
                AnimationClip anim = Resources.Load<AnimationClip>(animString);
                anim.legacy = true;
                toastCanvas.GetComponentInChildren<Animation>().Stop();
                toastCanvas.GetComponent<Animation>().AddClip(anim, animString);
                toastCanvas.GetComponentInChildren<Animation>().clip = anim;
                toastCanvas.GetComponentInChildren<Animation>().Play();


                AudioSource AS = toastCanvas.GetComponentInChildren<AudioSource>();
                switch (sound)
                {
                    case Sound.NONE:
                        break;
                    case Sound.BEEP:
                        AS.clip = Resources.Load<AudioClip>("AlertAudio2");
                        AS.Play();
                        break;
                    case Sound.CHIME:
                        AS.clip = Resources.Load<AudioClip>("DingAudio2");
                        AS.Play();
                        break;
                    case Sound.WARNING:
                        AS.clip = Resources.Load<AudioClip>("WarningAudio2");
                        AS.Play();
                        break;
                }
            }
        }


        // -------------- TALKING TOASTS using------------------------ //
        public static void Show(MonoBehaviour caller, string message, int duration, Type type, string textToSpeech)
        {

            if (isActive)
            {
                //Debug.Log("ENQUEUED ONE BIG");
                toastQueue.Enqueue(new ToastHolder(caller, message, duration, type, DEFAULT_SIZE, Gravity.BOTTOM, Sound.TEXTTOSPEECH, textToSpeech));
                return;
            }
            else
            {
                //Show the regular toast and then increase the size.
                Show(caller, message, duration, type);

                try
                {
                    // destroySpeech();  you cannot cancel out old speeches this way; it will crash.
                    if (!hardDisableTextToSpeechOutput)
                        addToSpeechQueue(textToSpeech);
                }
                catch (Exception e)
                {
                    Debug.Log("Speech error: " + e);
                }
            }
        }

        public static void Show(MonoBehaviour caller, string message, int duration, Type type, int size, string textToSpeech)
        {

            if (isActive)
            {
                toastQueue.Enqueue(new ToastHolder(caller, message, duration, type, size, Gravity.BOTTOM, Sound.TEXTTOSPEECH, textToSpeech));
                return;
            }
            else
            {
                toastCanvas.GetComponentInChildren<Text>().fontSize = size;
                //Show the regular toast and then increase the size.
                Show(caller, message, duration, type);
                try
                {
                    // destroySpeech();  you cannot cancel out old speeches this way; it will crash.
                    if (!hardDisableTextToSpeechOutput)
                        addToSpeechQueue(textToSpeech);
                }
                catch (Exception e)
                {
                    Debug.Log("Speech error: " + e);
                }
            }
        }

        public static void Show(MonoBehaviour caller, string message, int duration, Type type, Gravity gravity, string textToSpeech)
        {

            if (isActive)
            {

                toastQueue.Enqueue(new ToastHolder(caller, message, duration, type, DEFAULT_SIZE, gravity, Sound.TEXTTOSPEECH, textToSpeech));
                return;
            }
            else
            {
                //Show the regular toast and then increase the size.
                Show(caller, message, duration, type);

                string animString = getGravityString(gravity);
                AnimationClip anim = Resources.Load<AnimationClip>(animString);
                anim.legacy = true;
                toastCanvas.GetComponentInChildren<Animation>().Stop();
                toastCanvas.GetComponent<Animation>().AddClip(anim, animString);
                toastCanvas.GetComponentInChildren<Animation>().clip = anim;
                toastCanvas.GetComponentInChildren<Animation>().Play();

                try
                {
                    // destroySpeech();  you cannot cancel out old speeches this way; it will crash.
                    if (!hardDisableTextToSpeechOutput)
                        addToSpeechQueue(textToSpeech);
                }
                catch (Exception e)
                {
                    Debug.Log("Speech error: " + e);
                }


            }
        }

        public static void Show(MonoBehaviour caller, string message, int duration, Type type, int size, Gravity gravity, string textToSpeech)
        {

            if (isActive)
            {

                toastQueue.Enqueue(new ToastHolder(caller, message, duration, type, size, gravity, Sound.TEXTTOSPEECH, textToSpeech));

                return;
            }
            else
            {



                //Show the regular toast and then increase the size.
                Show(caller, message, duration, type);

                toastCanvas.GetComponentInChildren<Text>().fontSize = size;

                string animString = getGravityString(gravity);
                AnimationClip anim = Resources.Load<AnimationClip>(animString);
                anim.legacy = true;
                toastCanvas.GetComponentInChildren<Animation>().Stop();
                toastCanvas.GetComponent<Animation>().AddClip(anim, animString);
                toastCanvas.GetComponentInChildren<Animation>().clip = anim;
                toastCanvas.GetComponentInChildren<Animation>().Play();

                try
                {
                    // destroySpeech();  you cannot cancel out old speeches this way; it will crash.
                    if (!hardDisableTextToSpeechOutput)
                        addToSpeechQueue(textToSpeech);
                }
                catch (Exception e)
                {
                    Debug.Log("Speech error: " + e);
                }

            }
        }





        static IEnumerator DestroyToast()
        {
            yield return new WaitForSeconds(durationSecs);
            GameObject.Destroy(toastCanvas);
            //Debug.Log("DELETED ONE");
            isActive = false;
            if (toastQueue.Count > 0)
            {
                ToastHolder topToast = toastQueue.Dequeue();
                Toast.Show(topToast.toastContext, topToast.toastMessage, topToast.toastDuration, topToast.toastType, topToast.toastSize, topToast.toastGravity, topToast.textToSpeech);
            }
        }

        private static string getGravityString(Gravity gravity)
        {
            switch (gravity)
            {
                case Gravity.BOTTOM:
                    return "Bottom";
                case Gravity.BOTTOM_RIGHT:
                    return "BottomRight";
                case Gravity.CENTER:
                    return "Middle";
                case Gravity.TOP:
                    return "Top";
                case Gravity.TOP_RIGHT:
                    return "TopRight";
            }
            return "Bottom";    //If something bad/wrong happens, just show the toast from the bottom.
        }


        /// <summary>
        /// Dismisses the current toast and dumps the entire toast queue
        /// </summary>
        /// <returns>A boolean, TRUE if a Toast was dismissed, FALSE if there was no active Toast to dismiss.</returns>
        public static bool Dismiss()
        {
            if (isActive)
            {
                isActive = false;
                GameObject.Destroy(toastCanvas);
                ctxt.StopCoroutine(currentTimer);
                toastQueue.Clear();
                return true;
            }
            else
                return false;
        }


        /// <summary>
        /// Dismisses the current toast being displayed, if any, and activates the next toast in the queue, if any.
        /// </summary>
        /// <returns>A boolean, TRUE if there was another toast to show and a toast to dismiss, FALSE if there is no toast to show or no toast to dismiss.</returns>
        public static bool DismissNext()
        {
            if (isActive)
            {
                isActive = false;
                GameObject.Destroy(toastCanvas);
                ctxt.StopCoroutine(currentTimer);
                if (toastQueue.Count > 0)
                {
                    ToastHolder topToast = toastQueue.Dequeue();
                    //if (topToast.toastSize > 0)
                    //    Toast.Show(topToast.toastContext, topToast.toastMessage, topToast.toastDuration, topToast.toastType, topToast.toastSize);
                    //else
                    //    Toast.Show(topToast.toastContext, topToast.toastMessage, topToast.toastDuration, topToast.toastType);
                    Toast.Show(topToast.toastContext, topToast.toastMessage, topToast.toastDuration, topToast.toastType, topToast.toastSize, topToast.toastGravity);
                    return true;
                }
                return false;
            }
            else
                return false;
        }
    }
}
