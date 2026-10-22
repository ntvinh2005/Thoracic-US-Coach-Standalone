using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace SMMARTS
{
    public class SubmitScript : ButtonTemplate_25
    {
        // Start is called before the first frame update


        // Update is called once per frame
        public void SubmitAllSelected()
        {
            QuestionButtons[] questionButtons = FindObjectsOfType<QuestionButtons>();
            foreach (var button in questionButtons)
            {
                if (button.selected)
                {

                    button.Submit();

                }
            }

        }
        public void SubmitAll()
        {
            QuestionButtons[] questionButtons = FindObjectsOfType<QuestionButtons>();
            foreach (var button in questionButtons)
            {
                button.Submit();
            }

        }
    }
}