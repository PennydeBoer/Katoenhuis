using UnityEngine;
using System.Collections.Generic;
using UnityEngine.UI;

public class RandomlyGeneratedTimer : MonoBehaviour
{
    [Header("Number Gameobjects tenthminute, minute, tenthsecond, second")]
    [SerializeField] private List<Image> Numbers;
    [SerializeField] private List<Sprite> numbersSprites;
    [SerializeField] private float timeInBetween;
    private float elapsedTime;

    void Update()
    {
        elapsedTime += Time.deltaTime;
        if (elapsedTime> timeInBetween)
        {
            elapsedTime = 0;
            int tempTenthMinute = Random.Range(0, 6);
            int tempMinute = Random.Range(0, 10);
            int tempTenthSecond = Random.Range(0, 6);
            int tempSecond = Random.Range(0, 10);
            Numbers[0].sprite = numbersSprites[tempTenthMinute];
            Numbers[1].sprite = numbersSprites[tempMinute];
            Numbers[2].sprite = numbersSprites[tempTenthSecond];
            Numbers[3].sprite = numbersSprites[tempSecond];
            for (int i = 0; i < Numbers.Count; i++)
            {
            float temp = Random.Range(0.7f,1f);
            Numbers[i].color = new Color(temp,temp, temp);
            }
        }
        
    }
}
