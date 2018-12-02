﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.Remoting.Messaging;
using Newtonsoft.Json;
using Unity.Mathematics;
#if UNITY_EDITOR
using UnityEditor;
#endif
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using ComputeBuffer = UnityEngine.ComputeBuffer;

public class NeuralNetworkModel
{
    public List<NeuralNetworkLayer> Layers;
    private InputLayer Input;
    private OutputLayer Output;
    private CommandBuffer cb;

    public void Load(
        string architecturePath = "Model/model_architecture",
        string weightsPath = "Model/model_weight")
    {
        TextAsset architexturetext = Resources.Load<TextAsset>("Model/model_terrain");
        var modeljson = JsonConvert.DeserializeObject<KerasJson>(architexturetext.text);
        LoadModel(modeljson.model.config);
        LoadWeight(modeljson.weights);
    }

    private void LoadModel(KerasLayersJson layersJson)
    {
        Layers = new List<NeuralNetworkLayer>();
        Dictionary<string, List<string>> inputNodes = new Dictionary<string, List<string>>();
        foreach (var layer in layersJson.layers)
        {
            switch (layer.class_name)
            {
                case "InputLayer":
                    Input = new InputLayer(layer.config);
                    //Layers.Add(new InputLayer(layer.config));
                    break;
                case "Conv2D":
                    Layers.Add(new Conv2D(layer.config));
                    if (layer.config.activation == "relu")
                    {
                        Layers.Add(new ReLU(layer.config));
                    }
                    if (layer.config.activation == "tanh")
                    {
                        Layers.Add(new Tanh(layer.config));
                    }
                    break;
                case "LeakyReLU":
                    Layers.Add(new LeakyReLU(layer.config));
                    break;
                case "BatchNormalization":
                    Layers.Add(new BatchNormalization(layer.config));
                    break;
                case "UpSampling2D":
                    Layers.Add(new UpSampling2D(layer.config));
                    break;
                case "Concatenate":
                    Layers.Add(new Concatenate(layer.config));
                    break;
            }
            if (layer.inbound_nodes.Count > 0)
            {
                List<string> inputs = layer.inbound_nodes[0].ConvertAll<string>(objs =>objs[0].ToString());
                inputNodes.Add(layer.name, inputs);
            }
        }
        Output = new OutputLayer(null);
        Output.InputLayersId = new List<int>(){ Layers.Count - 1};
    }

    private void LoadWeight(List<KerasLayerWeightJson> weights)
    {
        int weightcount = 0;
        for (int i = 0; i < Layers.Count; i++)
        {
            if (Layers[i] is Conv2D)
            {
                Layers[i].LoadWeight(new KerasLayerWeightJson[2]
                {
                    weights[weightcount],
                    weights[weightcount + 1]
                });
                weightcount += 2;
            }
            if (Layers[i] is BatchNormalization)
            {
                Layers[i].LoadWeight(new KerasLayerWeightJson[4] {
                    weights[weightcount],
                    weights[weightcount + 1],
                    weights[weightcount + 2],
                    weights[weightcount + 3]
                });
                weightcount += 4;
            }
        }
    }

    public void Init(int height, int width)
    {
        Input.Init(new int4(height, width, 3, 0));
        Layers[0].Init(new int4(height, width, 3, 0));
        for (int i = 1; i < Layers.Count; i++)
        {
            Layers[i].Init(Layers[i - 1].OutputShape);
        }
        Output.Init(Layers[Layers.Count - 1].OutputShape);
    }
    private int _height, _width;

    public void Setup(CommandBuffer cmd, RenderTargetIdentifier src, int height, int width)
    {
        if (_height != height || _width != width)
        {
            Init(height, width);
            _height = height;
            _width = width;
        }

        Input.src = src;

        cb = cmd;
    }

    public RenderTexture Predict()
    {
        Input.Run(null, cb);
        Layers[0].Run(new object[1] { Input.Output }, cb);
        for (int i = 1; i < Layers.Count; i++)
        {
            Layers[i].Run(new object[1] { Layers[i - 1].Output }, cb);
        }
        Output.Run(new object[1] { Layers[Layers.Count - 1].Output }, cb);
        return Output.outputTex;
    }

    public void Release()
    {
        foreach (var layer in Layers)
        {
            layer.Release();
        }
        Input.Release();
        Output.Release();
    }
}

[System.Serializable]
public class NeuralNetworkLayer
{
    public string Name;
    public List<int> InputLayersId;
    public int4 InputShape;
    public int4 OutputShape;
    public int4 WeightShape;
    public object Output;
    [SerializeField]
    protected int KernelId;
    public NeuralNetworkLayer(KerasLayerConfigJson config)
    {
        if (config != null)
            Name = config.name;
    }

    public virtual void LoadWeight(KerasLayerWeightJson[] weights)
    {

    }

    public virtual void Run(object[] input, CommandBuffer cmd)
    {
        Output = input[0];
    }

    public virtual void Init(int4 inputShape)
    {
        InputShape = inputShape;
        OutputShape = inputShape;
    }

    public virtual void Release()
    {
    }
}

public class InputLayer: NeuralNetworkLayer
{
    private ComputeBuffer outputbuffer;
    public RenderTargetIdentifier src;
    public InputLayer(KerasLayerConfigJson config) : base(config)
    {
        KernelId = NeuralNetworkComputeShader.Instance.Kernel("InputLayer");
    }

    public override void Run(object[] input, CommandBuffer cmd)
    {
        cmd.SetComputeTextureParam(NeuralNetworkComputeShader.Instance.Shader, KernelId, "InputImage", src);
        cmd.SetComputeBufferParam(NeuralNetworkComputeShader.Instance.Shader, KernelId, "LayerOutput", outputbuffer);
        cmd.SetComputeIntParams(NeuralNetworkComputeShader.Instance.Shader, "InputShape", new int[3]
        {
            InputShape.x,
            InputShape.y,
            InputShape.z
        });
        cmd.SetComputeIntParams(NeuralNetworkComputeShader.Instance.Shader, "InputShapeIdMultiplier", new int[3]
        {
            InputShape.y * InputShape.z,
            InputShape.z,
            1
        });
        cmd.DispatchCompute(NeuralNetworkComputeShader.Instance.Shader, KernelId, InputShape.x / 8, InputShape.y / 8, 1);
    }

    public override void Init(int4 inputShape)
    {
        base.Init(inputShape);
        outputbuffer?.Release();
        outputbuffer = new ComputeBuffer(OutputShape.x * OutputShape.y * OutputShape.z, sizeof(float));
        Output = outputbuffer;
    }

    public override void Release()
    {
        outputbuffer?.Release();
    }
}
[System.Serializable]
public class Conv2D : NeuralNetworkLayer
{
    public int Filters;
    public int2 KernalSize;
    public int2 Stride;
    private ComputeBuffer outputbuffer;
    private ComputeBuffer weightbuffer;
    public Conv2D(KerasLayerConfigJson config) : base(config)
    {
        Filters = config.filters;
        KernalSize = new int2(config.kernel_size[0], config.kernel_size[1]);
        Stride = new int2(config.strides[0], config.strides[1]);
        KernelId = NeuralNetworkComputeShader.Instance.Kernel("Conv2D");
    }

    public override void LoadWeight(KerasLayerWeightJson[] weightsKernel)
    {
        WeightShape = new int4(weightsKernel[0].shape[0],
            weightsKernel[0].shape[1],
            weightsKernel[0].shape[2],
            weightsKernel[0].shape[3]);
        int kernel_weight_length = WeightShape.x * WeightShape.y * WeightShape.z * WeightShape.w;
        int bias_weight_length = WeightShape.w;
        float[] Weights = new float[kernel_weight_length + bias_weight_length];
        for (int i = 0; i < WeightShape.x; i++)
        {
            for (int j = 0; j < WeightShape.y; j++)
            {
                for (int k = 0; k < WeightShape.z; k++)
                {
                    for (int w = 0; w < WeightShape.w; w++)
                    {
                        int arrayindex = i * WeightShape.y * WeightShape.z * WeightShape.w +
                                         j * WeightShape.z * WeightShape.w +
                                         k * WeightShape.w +
                                         w;
                        Weights[arrayindex] = weightsKernel[0].kernelweight[i, j, k, w];
                    }
                }
            }
        }
        Array.Copy(weightsKernel[1].arrayweight, 0, Weights, kernel_weight_length, bias_weight_length);
        weightbuffer?.Release();
        weightbuffer = new ComputeBuffer(kernel_weight_length + bias_weight_length, sizeof(float));
        weightbuffer.SetData(Weights);
    }

    public override void Init(int4 inputShape)
    {
        InputShape = inputShape;
        OutputShape = inputShape;
        OutputShape.xy /= Stride;
        OutputShape.z = Filters;
        outputbuffer?.Release();
        outputbuffer = new ComputeBuffer(OutputShape.x * OutputShape.y * OutputShape.z, sizeof(float));
        Output = outputbuffer;
    }

    public override void Release()
    {
        weightbuffer?.Release();
        outputbuffer?.Release();
    }

    public override void Run(object[] input, CommandBuffer cmd)
    {
        cmd.SetComputeBufferParam(NeuralNetworkComputeShader.Instance.Shader, KernelId, "LayerInput0", input[0] as ComputeBuffer);
        cmd.SetComputeBufferParam(NeuralNetworkComputeShader.Instance.Shader, KernelId, "LayerOutput", outputbuffer);
        cmd.SetComputeBufferParam(NeuralNetworkComputeShader.Instance.Shader, KernelId, "Weights", weightbuffer);
        cmd.SetComputeIntParams(NeuralNetworkComputeShader.Instance.Shader, "InputShape", new int[3]
        {
            InputShape.x,
            InputShape.y,
            InputShape.z
        });
        cmd.SetComputeIntParams(NeuralNetworkComputeShader.Instance.Shader, "InputShapeIdMultiplier", new int[3]
        {
            InputShape.y * InputShape.z,
            InputShape.z,
            1
        });
        cmd.SetComputeIntParams(NeuralNetworkComputeShader.Instance.Shader, "OutputShape", new int[3]
        {
            OutputShape.x,
            OutputShape.y,
            OutputShape.z
        });
        cmd.SetComputeIntParams(NeuralNetworkComputeShader.Instance.Shader, "OutputShapeIdMultiplier", new int[3]
        {
            OutputShape.y * OutputShape.z,
            OutputShape.z,
            1
        });
        cmd.SetComputeIntParams(NeuralNetworkComputeShader.Instance.Shader, "WeightsShape", new int[4]
        {
            KernalSize.x,
            KernalSize.y,
            InputShape.z,
            Filters
        });
        cmd.SetComputeIntParams(NeuralNetworkComputeShader.Instance.Shader, "WeightsShapeIdMultiplier", new int[4]
        {
            KernalSize.y * InputShape.z * Filters,
            InputShape.z * Filters,
            Filters,
            1
        });
        cmd.SetComputeIntParams(NeuralNetworkComputeShader.Instance.Shader, "Stride", new int[2]
        {
            Stride.x,
            Stride.y
        });
        int group = Mathf.CeilToInt(OutputShape.z / 32.0f);

        cmd.DispatchCompute(NeuralNetworkComputeShader.Instance.Shader, KernelId, OutputShape.x, OutputShape.y, group);
    }
}

public class ReLU : NeuralNetworkLayer
{
    protected ComputeBuffer outputbuffer;
    public ReLU(KerasLayerConfigJson config) : base(config)
    {
        KernelId = NeuralNetworkComputeShader.Instance.Kernel("ReLU");
    }

    public override void Init(int4 inputShape)
    {
        base.Init(inputShape);
        outputbuffer?.Release();
        outputbuffer = new ComputeBuffer(OutputShape.x * OutputShape.y * OutputShape.z, sizeof(float));
        Output = outputbuffer;
    }

    public override void Release()
    {
        outputbuffer?.Release();
    }

    public override void Run(object[] input, CommandBuffer cmd)
    {
        cmd.SetComputeBufferParam(NeuralNetworkComputeShader.Instance.Shader, KernelId, "LayerInput0", input[0] as ComputeBuffer);
        cmd.SetComputeBufferParam(NeuralNetworkComputeShader.Instance.Shader, KernelId, "LayerOutput", outputbuffer);
        cmd.DispatchCompute(NeuralNetworkComputeShader.Instance.Shader, KernelId, OutputShape.x * OutputShape.y * OutputShape.z / 32, 1, 1);
    }
}

public class Tanh : NeuralNetworkLayer
{
    private ComputeBuffer outputbuffer;
    public Tanh(KerasLayerConfigJson config) : base(config)
    {
        KernelId = NeuralNetworkComputeShader.Instance.Kernel("Tanh");
    }

    public override void Init(int4 inputShape)
    {
        base.Init(inputShape);
        outputbuffer?.Release();
        outputbuffer = new ComputeBuffer(OutputShape.x * OutputShape.y * OutputShape.z, sizeof(float));
        Output = outputbuffer;
    }

    public override void Release()
    {
        outputbuffer?.Release();
    }

    public override void Run(object[] input, CommandBuffer cmd)
    {
        cmd.SetComputeBufferParam(NeuralNetworkComputeShader.Instance.Shader, KernelId, "LayerInput0", input[0] as ComputeBuffer);
        cmd.SetComputeBufferParam(NeuralNetworkComputeShader.Instance.Shader, KernelId, "LayerOutput", outputbuffer);
        cmd.DispatchCompute(NeuralNetworkComputeShader.Instance.Shader, KernelId, OutputShape.x * OutputShape.y * OutputShape.z / 32, 1, 1);
    }
}

public class LeakyReLU : ReLU
{
    public float Alpha;
    public LeakyReLU(KerasLayerConfigJson config) : base(config)
    {
        Alpha = config.alpha;
        KernelId = NeuralNetworkComputeShader.Instance.Kernel("LeakyReLU");
    }

    public override void Run(object[] input, CommandBuffer cmd)
    {
        cmd.SetComputeBufferParam(NeuralNetworkComputeShader.Instance.Shader, KernelId, "LayerInput0", input[0] as ComputeBuffer);
        cmd.SetComputeBufferParam(NeuralNetworkComputeShader.Instance.Shader, KernelId, "LayerOutput", outputbuffer);
        cmd.SetComputeFloatParam(NeuralNetworkComputeShader.Instance.Shader, "Alpha", Alpha);
        cmd.DispatchCompute(NeuralNetworkComputeShader.Instance.Shader, KernelId, OutputShape.x * OutputShape.y * OutputShape.z / 32, 1, 1);
    }
}
[System.Serializable]
public class BatchNormalization : NeuralNetworkLayer
{
    private ComputeBuffer weightbuffer;
    private ComputeBuffer outputbuffer;
    public BatchNormalization(KerasLayerConfigJson config) : base(config)
    {
        KernelId = NeuralNetworkComputeShader.Instance.Kernel("BatchNormalization");
    }

    public override void LoadWeight(KerasLayerWeightJson[] weightsKernel)
    {
        WeightShape.x = weightsKernel[0].shape[0];
        float[] Weights = new float[WeightShape.x * 4];
        for (int i = 0; i < WeightShape.x; i++)
        {
            Weights[i * 4]     = weightsKernel[0].arrayweight[i];
            Weights[i * 4 + 1] = weightsKernel[1].arrayweight[i];
            Weights[i * 4 + 2] = weightsKernel[2].arrayweight[i];
            Weights[i * 4 + 3] = weightsKernel[3].arrayweight[i];
        }
        weightbuffer?.Release();
        weightbuffer = new ComputeBuffer(WeightShape.x * 4, sizeof(float));
        weightbuffer.SetData(Weights);
    }

    public override void Init(int4 inputShape)
    {
        base.Init(inputShape);
        outputbuffer?.Release();
        outputbuffer = new ComputeBuffer(OutputShape.x * OutputShape.y * OutputShape.z, sizeof(float));
        Output = outputbuffer;
    }

    public override void Release()
    {
        outputbuffer?.Release();
        weightbuffer?.Release();
    }

    public override void Run(object[] input, CommandBuffer cmd)
    {
        cmd.SetComputeBufferParam(NeuralNetworkComputeShader.Instance.Shader, KernelId, "LayerInput0", input[0] as ComputeBuffer);
        cmd.SetComputeBufferParam(NeuralNetworkComputeShader.Instance.Shader, KernelId, "LayerOutput", outputbuffer);
        cmd.SetComputeBufferParam(NeuralNetworkComputeShader.Instance.Shader, KernelId, "Weights", weightbuffer);
        cmd.SetComputeIntParams(NeuralNetworkComputeShader.Instance.Shader, "InputShape", new int[3]
        {
            InputShape.x,
            InputShape.y,
            InputShape.z
        });
        cmd.SetComputeIntParams(NeuralNetworkComputeShader.Instance.Shader, "OutputShape", new int[3]
        {
            OutputShape.x,
            OutputShape.y,
            OutputShape.z
        });
        cmd.DispatchCompute(NeuralNetworkComputeShader.Instance.Shader, KernelId, OutputShape.x * OutputShape.y / 32, OutputShape.z, 1);
    }
}

public class Concatenate : NeuralNetworkLayer
{
    public Concatenate(KerasLayerConfigJson config) : base(config)
    {
    }
}

public class UpSampling2D : NeuralNetworkLayer
{
    public int2 Size;
    private ComputeBuffer outputbuffer;
    public UpSampling2D(KerasLayerConfigJson config) : base(config)
    {
        Size = new int2(config.size[0], config.size[1]);
        KernelId = NeuralNetworkComputeShader.Instance.Kernel("UpSampling2D");
    }
    public override void Init(int4 inputShape)
    {
        InputShape = inputShape;
        OutputShape = inputShape;
        OutputShape.xy *= Size;
        outputbuffer?.Release();
        outputbuffer = new ComputeBuffer(OutputShape.x * OutputShape.y * OutputShape.z, sizeof(float));
        Output = outputbuffer;
    }

    public override void Release()
    {
        outputbuffer?.Release();
    }

    public override void Run(object[] input, CommandBuffer cmd)
    {
        cmd.SetComputeBufferParam(NeuralNetworkComputeShader.Instance.Shader, KernelId, "LayerInput0", input[0] as ComputeBuffer);
        cmd.SetComputeBufferParam(NeuralNetworkComputeShader.Instance.Shader, KernelId, "LayerOutput", outputbuffer);
        cmd.SetComputeIntParams(NeuralNetworkComputeShader.Instance.Shader, "InputShape", new int[3]
        {
            InputShape.x,
            InputShape.y,
            InputShape.z
        });
        cmd.SetComputeIntParams(NeuralNetworkComputeShader.Instance.Shader, "InputShapeIdMultiplier", new int[3]
        {
            InputShape.y * InputShape.z,
            InputShape.z,
            1
        });
        cmd.SetComputeIntParams(NeuralNetworkComputeShader.Instance.Shader, "OutputShape", new int[3]
        {
            OutputShape.x,
            OutputShape.y,
            OutputShape.z
        });
        cmd.SetComputeIntParams(NeuralNetworkComputeShader.Instance.Shader, "OutputShapeIdMultiplier", new int[3]
        {
            OutputShape.y * OutputShape.z,
            OutputShape.z,
            1
        });
        cmd.SetComputeIntParams(NeuralNetworkComputeShader.Instance.Shader, "Size", new int[2]
        {
            Size.x,
            Size.y
        });
        cmd.DispatchCompute(NeuralNetworkComputeShader.Instance.Shader, KernelId, OutputShape.x / 8, OutputShape.y / 8, OutputShape.z);
    }
}

public class OutputLayer : NeuralNetworkLayer
{
    public RenderTexture outputTex;
    public OutputLayer(KerasLayerConfigJson config) : base(config)
    {
        KernelId = NeuralNetworkComputeShader.Instance.Kernel("OutputLayer");
    }

    public override void Run(object[] input, CommandBuffer cmd)
    {
        cmd.SetComputeBufferParam(NeuralNetworkComputeShader.Instance.Shader, KernelId, "LayerInput0", input[0] as ComputeBuffer);
        cmd.SetComputeTextureParam(NeuralNetworkComputeShader.Instance.Shader, KernelId, "OutputImage", outputTex);
        cmd.SetComputeIntParams(NeuralNetworkComputeShader.Instance.Shader, "InputShape", new int[3]
        {
            InputShape.x,
            InputShape.y,
            InputShape.z
        });
        cmd.SetComputeIntParams(NeuralNetworkComputeShader.Instance.Shader, "InputShapeIdMultiplier", new int[3]
        {
            InputShape.y * InputShape.z,
            InputShape.z,
            1
        });
        cmd.DispatchCompute(NeuralNetworkComputeShader.Instance.Shader, KernelId, OutputShape.x / 8, OutputShape.y / 8, 1);
    }

    public override void Init(int4 inputShape)
    {
        base.Init(inputShape);
        outputTex?.Release();
        outputTex = new RenderTexture(OutputShape.y, OutputShape.x, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
        outputTex.enableRandomWrite = true;
        outputTex.Create();
    }

    public override void Release()
    {
        outputTex?.Release();
    }
}

public class NeuralNetworkComputeShader
{
    private static NeuralNetworkComputeShader _instance;
    public static NeuralNetworkComputeShader Instance
    {
        get {
            if (_instance == null)
            {
                _instance = new NeuralNetworkComputeShader();
                _instance.Init();
            }
            return _instance;
        }

    }

    public ComputeShader Shader;
    private string shaderpath = "NeuralNetworkLayer";
    private int Conv2DKernel, LeakyReluKernel, BatchNormalizationKernel, InputLayerKernel, 
        OutputLayerKernel, UpSampling2DKernel, ReluKernel, TanhKernel;

    private void Init()
    {
        Shader = Resources.Load<ComputeShader>(shaderpath);
        Conv2DKernel = Shader.FindKernel("Conv2D");
        LeakyReluKernel = Shader.FindKernel("LeakyReLU");
        BatchNormalizationKernel = Shader.FindKernel("BatchNormalization");
        InputLayerKernel = Shader.FindKernel("InputLayer");
        OutputLayerKernel = Shader.FindKernel("OutputLayer");
        UpSampling2DKernel = Shader.FindKernel("UpSampling2D");
        ReluKernel = Shader.FindKernel("ReLU");
        TanhKernel = Shader.FindKernel("Tanh");
    }

    public int Kernel(string name)
    {
        switch (name)
        {
            case ("Conv2D"):
                return Conv2DKernel;
            case ("LeakyReLU"):
                return LeakyReluKernel;
            case ("BatchNormalization"):
                return BatchNormalizationKernel;
            case ("InputLayer"):
                return InputLayerKernel;
            case ("OutputLayer"):
                return OutputLayerKernel;
            case ("UpSampling2D"):
                return UpSampling2DKernel;
            case ("ReLU"):
                return ReluKernel;
            case ("Tanh"):
                return TanhKernel;
            default:
                return -1;
        }
    }
}