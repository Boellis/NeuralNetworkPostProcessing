﻿// neural network post-processing

using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace NNPP
{
    [System.Serializable]
    public class BatchNormalization : NNLayerBase
    {
        public float[] weightcache;
        private ComputeBuffer weightbuffer;
        private ComputeBuffer outputbuffer;
        public BatchNormalization() : base()
        {
            KernelId = NNCompute.Instance.Kernel("BatchNormalization");
        }

        
        public override void FromCache()
        {
            weightbuffer = new ComputeBuffer(weightcache.Length, sizeof(float));
            weightbuffer.SetData(weightcache);
            weightcache = null;
        }
        /*
        public override void LoadWeight(KerasLayerWeightJson[] weightsKernel)
        {
            WeightShape.x = weightsKernel[0].shape[0];
            float[] Weights = new float[(int)WeightShape.x * 4];
            for (int i = 0; i < WeightShape.x; i++)
            {
                Weights[i * 4] = weightsKernel[0].arrayweight[i];
                Weights[i * 4 + 1] = weightsKernel[1].arrayweight[i];
                Weights[i * 4 + 2] = weightsKernel[2].arrayweight[i];
                Weights[i * 4 + 3] = weightsKernel[3].arrayweight[i];
            }
            if(weightbuffer !=null)
                weightbuffer.Release();
            weightbuffer = new ComputeBuffer((int)WeightShape.x * 4, sizeof(float));
            weightbuffer.SetData(Weights);
        }*/

        public override void Init(Vector3Int inputShape)
        {
            base.Init(inputShape);
            if (outputbuffer != null)
                outputbuffer.Release();
            outputbuffer = new ComputeBuffer(OutputShape.x * OutputShape.y * OutputShape.z, sizeof(float));
            Output = outputbuffer;
        }

        public override void Release()
        {
            if (outputbuffer != null)
                outputbuffer.Release();
            if (weightbuffer != null)
                weightbuffer.Release();
        }

        public override void Run(object[] input)
        {
            NNCompute.Instance.Shader.SetBuffer(KernelId, "LayerInput0", input[0] as ComputeBuffer);
            NNCompute.Instance.Shader.SetBuffer(KernelId, "LayerOutput", outputbuffer);
            NNCompute.Instance.Shader.SetBuffer(KernelId, "Weights", weightbuffer);
            NNCompute.Instance.Shader.SetInts("InputShapeIdMultiplier", new int[3]
            {
                InputShape.y * InputShape.z,
                InputShape.z,
                1
            });
            NNCompute.Instance.Shader.SetInts("InputShape", new int[3]
            {
                InputShape.x,
                InputShape.y,
                InputShape.z
            });
            NNCompute.Instance.Shader.SetInts("OutputShape", new int[3]
            {
                OutputShape.x,
                OutputShape.y,
                OutputShape.z
            });
            //Threadgroup could exceed 65536 in high resolution, expand to 2d.
            NNCompute.Instance.Shader.Dispatch(
                KernelId, 
                Mathf.CeilToInt(OutputShape.x / 8.0f),
                Mathf.CeilToInt(OutputShape.y / 4.0f), OutputShape.z);
        }
    }
}