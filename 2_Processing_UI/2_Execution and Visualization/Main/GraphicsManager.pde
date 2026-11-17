/*ETactileKit - Visualization and Execution programme

GraphicsManager :- used to give visual feedback to user

Functions:  setupGraphics()
            displayInstructions()
            resetElectrodes()
            drawElectrodes()
            visualizeAndExecute()
            executeOnly()
            visualizeOnly()
            NoAny()
            showpulse()
*/

class GraphicsManager {
  private final int WINDOW_SIZE_X = 1000;   //length of the window
  private final int WINDOW_SIZE_Y = 900;    //height of the window
  private final int Electode_Size = 25;     //diameter of an electrode
  
  JSONArray  coordinates;
  JSONArray  electrodes;
  float[][]  electrodePositions;

  //Setup the Window
  void setupGraphics() {
    size(WINDOW_SIZE_X, WINDOW_SIZE_Y);
  }
  
  //Display the instructions for user
  void displayInstructions() {
    println("Now pusle height is set to 0. Press UP and DOWN keys to adjust volume.");
    println("Press RIGHT and LEFT keys to adjust pulse width.");
    println("Press p to change the polarity.");
    println("Press space_bar to reset pulse height and pulse width");
  }
  
  //Reset the electrodes
  void drawElectrodes() {
    electrodePositions = new float[dataLoader.electrodes_number][2];
    
    noStroke();
    textSize(27);
    
    for (int i=0; i < dataLoader.electrodes.size(); i++){
      JSONObject  electrode  = dataLoader.electrodes.getJSONObject(i);
      float       x          = electrode.getFloat("x")*10+500;
      float       y          = 900-(electrode.getFloat("y")*10+500);
      int         number     = i;
      
      electrodePositions[i][0] = x;
      electrodePositions[i][1] = y;
      
      if (eTactile.switch_state[i] == 1){
        fill(255,0,0);
      }
      else{
        fill(0,0,255);
      }
      
      textSize(15);
      text(number, x+8, y-10);
      ellipseMode(CENTER);
      ellipse(x, y, Electode_Size, Electode_Size);
      textSize(27);
    }
  }
  
  
  void visualizeBoardShape(){
    // Set color for the shape outline
    stroke(0);
    strokeWeight(2);
    fill(200, 200, 200); 
    
    // Start drawing the shape
    beginShape();
    
    // Loop through the coordinates and add each vertex to the shape
    for (int i = 0; i < dataLoader.coordinates.size(); i++) {
      JSONObject point = dataLoader.coordinates.getJSONObject(i);
      float      x     = point.getFloat("x")*10+500;
      float      y     = 900-(point.getFloat("y")*10+500);
      vertex(x, y); // Add each vertex
    }
    
    // End and close the shape
    endShape(CLOSE);
  }
  

  
  //If execution: true & simulation: true
  void visualizeAndExecute(){
    int amplitude = eTactile.amplitude;
    int width_    = eTactile.width_;
    text("Amplitude: " + amplitude, 10, WINDOW_SIZE_Y - 60);
    text("Pulse Width: " + width_, 10, WINDOW_SIZE_Y - 30);
  }
  
  //If execution: true & simulation: false
  void executeOnly(){
    int amplitude  = eTactile.amplitude;
    int width_     = eTactile.width_;
    textSize(40);
    text("No Visualization. Execution Only.", 5 * WINDOW_SIZE_X / 32, WINDOW_SIZE_Y / 2);
    text("Amplitude: " + amplitude, 5 * WINDOW_SIZE_X / 32, WINDOW_SIZE_Y / 2 + 60);
    text("Pulse Width: " + width_, 5 * WINDOW_SIZE_X / 32, WINDOW_SIZE_Y / 2 + 90);
  }
  
  //If execution: false & simulation: true
  void visualizeOnly(){
    textSize(32);
    text("No execution. Visualization Only", WINDOW_SIZE_X / 5, WINDOW_SIZE_Y - 60);
    noStroke();
    textSize(27);
  }
  
  //If execution: false & simulation: true
  void noAny(){
    textSize(32);
    text("No execution or Visualization activated!", 5 * WINDOW_SIZE_X / 32, WINDOW_SIZE_Y / 2.5);
    textSize(28);
    text("Please check your .json (pattern) file", 5 * WINDOW_SIZE_X / 25, WINDOW_SIZE_Y / 2);
  }
  
  //Visual indicator of the pules height and pulse width
  void showPulse(){
    int amplitude  = eTactile.amplitude;
    int width_     = eTactile.width_;
    int h,w;
    noFill();
    stroke(0,255,0);
    strokeWeight(4);
    h = WINDOW_SIZE_Y * amplitude / (260*4);
    w = WINDOW_SIZE_X/2*width_/(1000*4);
    rect(WINDOW_SIZE_X/2,WINDOW_SIZE_Y-h,w,h);
    textSize(28);
  }
  
}
