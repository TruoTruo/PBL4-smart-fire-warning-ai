from ultralytics import YOLO

def main():
    model = YOLO("yolo11n.pt")

    results = model.train(
        data="D:/Tai Lieu DUT/2026-2027/PBL4/NewResearch/data.yaml",
        
        epochs=100,            
        imgsz=640,             
        batch=16,             
        optimizer="SGD",      
        lr0=0.01,              
        momentum=0.8,          
        weight_decay=0.005,    
        warmup_epochs=3.0,     
        warmup_bias_lr=0.1,    
        amp=True,              
        device=0,              
        workers=2,   
                  
        project="fire_detection_paper_replication_true", 
        name="yolov11n_paper_config"
    )

if __name__ == '__main__':
    main()