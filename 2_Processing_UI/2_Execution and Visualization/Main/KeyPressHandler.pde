/*ETactileKit - Visualization and Execution programme

KeyPressHandler :- used to handle the keyboard inputs

Functions:  handleKeyPress()
            getVolume()
            getWidth()
*/


class KeyPressHandler {
  
  private int volume;
  private int width_;
  private int polarity;

  // Constructor
  KeyPressHandler() {
    volume = 0;
    width_ = 50;
    polarity = 1;
  }

  // Method to handle key presses and update Volume and Width
  void handleKeyPress(char key) {
    if (key == CODED) {
      if (keyCode == UP) {
        volume = volume + 1;
      } 
      else if (keyCode == DOWN) {
        volume = volume - 1;
      } 
      else if (keyCode == RIGHT) {
        width_ = width_ + 1;
      } 
      else if (keyCode == LEFT) {
        width_ = width_ -1;
      } 
        
      if(volume > 250)   volume = 250;
      if(volume < 0)     volume = 0;
      if(width_ > 250)   width_ = 250;
      if(width_ < 0)     width_ = 0;
        
      print("Volume: ",volume);
      println(", Width: ",width_);
    } 
    
    else if (key == ' '){
      volume    = 0;
      width_    = 50;
      polarity  = 1;
    } 
    
    else if (key == 'l') {
      volume = volume + 5;
      if(volume > 250) volume = 250;
      print("Volume: ",volume);
    }
    
    else if (key == 'k') {
      volume = volume + 1;
      if(volume > 250) volume = 250;
      print("Volume: ",volume);
    }
    
    else if (key == 'j') {
      volume = volume - 1;
      if(volume <0) volume = 0;
      print("Volume: ",volume);
    }
    
    else if (key == 'h') {
      volume = volume - 5;
      if(volume <0) volume = 0;
      print("Volume: ",volume);
    }
    
    else if (key == 'a') {
      polarity = 1;
    }
    
    else if (key == 'c') {
      polarity = 0;
    }
  }

  // Getter method for Volume
  int getVolume() {
    return volume;
  }

  // Getter method for Width
  int getWidth() {
    return width_;
  }

  int getPolarity() {
    return polarity;
  }
}
